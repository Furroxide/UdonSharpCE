using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Inlines very small methods to reduce call overhead.
    /// 
    /// This optimizer identifies methods that:
    /// - Are expression-bodied or have a single return statement
    /// - Have no more than MaxStatements statements
    /// - Are private and non-virtual
    /// - Are called at least MinCallCount times
    /// - Have no ref/out parameters
    /// 
    /// Example:
    /// private float Square(float x) =&gt; x * x;
    /// ...
    /// float result = Square(value);
    /// →
    /// float result = value * value;
    /// </summary>
    internal class TinyMethodInliningOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT004";
        public string OptimizerName => "Tiny Method Inlining";
        public string Description => "Inlines very small methods to eliminate call overhead.";
        public bool IsEnabledByDefault => true;
        public int Priority => 120; // After loop unrolling

        private const int MaxStatements = 2;
        private const int MinCallCount = 2;

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var root = tree.GetRoot();
            bool anyChanges = false;

            // Process each class separately to avoid cross-class method name collisions
            var rewriter = new ClassScopedRewriter(context, ref anyChanges);
            var newRoot = rewriter.Visit(root);

            if (anyChanges)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private bool IsInlinable(MethodDeclarationSyntax method)
        {
            // Must be private or internal and non-virtual
            var modifiers = method.Modifiers;
            bool hasPrivateOrInternal = modifiers.Any(m =>
                m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.InternalKeyword));

            if (!hasPrivateOrInternal)
                return false;

            if (modifiers.Any(m =>
                    m.IsKind(SyntaxKind.VirtualKeyword) ||
                    m.IsKind(SyntaxKind.AbstractKeyword) ||
                    m.IsKind(SyntaxKind.OverrideKeyword) ||
                    m.IsKind(SyntaxKind.StaticKeyword)))
                return false;

            // No ref/out parameters
            foreach (var param in method.ParameterList.Parameters)
            {
                if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)))
                    return false;
            }

            // Must be expression-bodied or have simple body
            if (method.ExpressionBody != null)
                return true;

            if (method.Body == null)
                return false;

            // Body must have at most MaxStatements statements
            if (method.Body.Statements.Count > MaxStatements)
                return false;

            // If body has statements, the last one must be a return
            if (method.Body.Statements.Count > 0)
            {
                var lastStatement = method.Body.Statements.Last();
                if (!(lastStatement is ReturnStatementSyntax))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Rewriter that processes each class separately to properly scope method inlining.
        /// </summary>
        private class ClassScopedRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private bool _anyChanges;

            public ClassScopedRewriter(OptimizationContext context, ref bool anyChanges)
            {
                _context = context;
                _anyChanges = anyChanges;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                try
                {
                    // Collect methods within this class only
                    var collector = new MethodCollector();
                    collector.Visit(node);

                    // Filter to only methods worth inlining
                    var optimizer = new TinyMethodInliningOptimizer();
                    var inlinableMethods = collector.Methods
                        .Where(m => optimizer.IsInlinable(m.Value.Declaration) && m.Value.CallCount >= MinCallCount)
                        .ToDictionary(m => m.Key, m => m.Value);

                    if (inlinableMethods.Count == 0)
                    {
                        // Still need to visit nested classes
                        return base.VisitClassDeclaration(node);
                    }

                    // Inline calls within this class
                    var inliner = new MethodInliner(inlinableMethods, _context);
                    var result = (ClassDeclarationSyntax)inliner.Visit(node);

                    if (inliner.ChangesMade)
                    {
                        _anyChanges = true;
                    }

                    return result;
                }
                catch (Exception)
                {
                    // If anything fails, just return the original node unchanged
                    return node;
                }
            }
        }

        private class MethodInfo
        {
            public MethodDeclarationSyntax Declaration { get; set; }
            public int CallCount { get; set; }
        }

        private class MethodCollector : CSharpSyntaxWalker
        {
            public Dictionary<string, MethodInfo> Methods { get; } = new Dictionary<string, MethodInfo>();

            public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var methodName = node.Identifier.Text;

                if (!Methods.ContainsKey(methodName))
                {
                    Methods[methodName] = new MethodInfo { Declaration = node, CallCount = 0 };
                }

                base.VisitMethodDeclaration(node);
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                var methodName = GetMethodName(node);
                if (methodName != null && Methods.TryGetValue(methodName, out var info))
                {
                    info.CallCount++;
                }

                base.VisitInvocationExpression(node);
            }

            private string GetMethodName(InvocationExpressionSyntax invocation)
            {
                switch (invocation.Expression)
                {
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.Text;

                    case MemberAccessExpressionSyntax memberAccess
                        when memberAccess.Expression is ThisExpressionSyntax:
                        return memberAccess.Name.Identifier.Text;

                    default:
                        return null;
                }
            }
        }

        private class MethodInliner : CSharpSyntaxRewriter
        {
            private readonly Dictionary<string, MethodInfo> _methods;
            private readonly OptimizationContext _context;
            public bool ChangesMade { get; private set; }

            public MethodInliner(Dictionary<string, MethodInfo> methods, OptimizationContext context)
            {
                _methods = methods;
                _context = context;
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                try
                {
                    var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);

                    var methodName = GetMethodName(visited);
                    if (methodName == null || !_methods.TryGetValue(methodName, out var methodInfo))
                        return visited;

                    var method = methodInfo.Declaration;

                    // Get the argument list
                    var arguments = visited.ArgumentList.Arguments;
                    var parameters = method.ParameterList.Parameters;

                    // Argument count must match
                    if (arguments.Count != parameters.Count)
                        return visited;

                    // Build parameter -> argument mapping
                    var parameterMap = new Dictionary<string, ExpressionSyntax>();
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        var paramName = parameters[i].Identifier.Text;
                        var argExpr = arguments[i].Expression;

                        // Only inline if argument is simple (avoid evaluating complex expressions multiple times)
                        if (!IsSimpleExpression(argExpr))
                            return visited;

                        parameterMap[paramName] = argExpr;
                    }

                    // Get the expression to inline
                    ExpressionSyntax inlineExpr = null;

                    if (method.ExpressionBody != null)
                    {
                        inlineExpr = method.ExpressionBody.Expression;
                    }
                    else if (method.Body != null && method.Body.Statements.Count > 0)
                    {
                        var lastStmt = method.Body.Statements.Last();
                        if (lastStmt is ReturnStatementSyntax returnStmt && returnStmt.Expression != null)
                        {
                            inlineExpr = returnStmt.Expression;
                        }
                    }

                    if (inlineExpr == null)
                        return visited;

                    // Replace parameters with arguments in the expression
                    var replacer = new ParameterReplacer(parameterMap);
                    var inlinedExpr = (ExpressionSyntax)replacer.Visit(inlineExpr);

                    // Wrap in parentheses if needed for precedence
                    inlinedExpr = SyntaxFactory.ParenthesizedExpression(inlinedExpr).WithTriviaFrom(node);

                    _context.RecordOptimization(
                        "CEOPT004",
                        $"Inlined method '{methodName}'",
                        node.GetLocation(),
                        visited.ToString(),
                        inlinedExpr.ToString());

                    ChangesMade = true;
                    return inlinedExpr;
                }
                catch (InvalidCastException)
                {
                    // If casting fails, just return the original node
                    return node;
                }
                catch (Exception)
                {
                    // If anything else fails, return the original node
                    return node;
                }
            }

            private string GetMethodName(InvocationExpressionSyntax invocation)
            {
                switch (invocation.Expression)
                {
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.Text;

                    case MemberAccessExpressionSyntax memberAccess
                        when memberAccess.Expression is ThisExpressionSyntax:
                        return memberAccess.Name.Identifier.Text;

                    default:
                        return null;
                }
            }

            private bool IsSimpleExpression(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case LiteralExpressionSyntax _:
                    case IdentifierNameSyntax _:
                    case ThisExpressionSyntax _:
                        return true;

                    case MemberAccessExpressionSyntax memberAccess:
                        return IsSimpleExpression(memberAccess.Expression);

                    case ParenthesizedExpressionSyntax paren:
                        return IsSimpleExpression(paren.Expression);

                    default:
                        return false;
                }
            }

            private class ParameterReplacer : CSharpSyntaxRewriter
            {
                private readonly Dictionary<string, ExpressionSyntax> _parameterMap;

                public ParameterReplacer(Dictionary<string, ExpressionSyntax> parameterMap)
                {
                    _parameterMap = parameterMap;
                }

                public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
                {
                    if (_parameterMap.TryGetValue(node.Identifier.Text, out var replacement))
                    {
                        return replacement.WithTriviaFrom(node);
                    }

                    return base.VisitIdentifierName(node);
                }
            }
        }
    }
}
