using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Moves computations that don't change inside a loop to before the loop.
    /// 
    /// This optimizer identifies expressions where:
    /// - All operands are loop-invariant (not modified within the loop)
    /// - The expression is guaranteed to execute (not behind conditionals)
    /// - The expression is pure (no side effects)
    /// 
    /// Example:
    /// for (int i = 0; i &lt; count; i++) {
    ///     float radius = Mathf.Sqrt(x * x + y * y);  // x, y not modified in loop
    ///     Process(positions[i], radius);
    /// }
    /// →
    /// float __licm_0 = Mathf.Sqrt(x * x + y * y);
    /// for (int i = 0; i &lt; count; i++) {
    ///     float radius = __licm_0;
    ///     Process(positions[i], radius);
    /// }
    /// </summary>
    internal class LoopInvariantCodeMotionOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT010";
        public string OptimizerName => "Loop Invariant Code Motion";
        public string Description => "Hoists invariant computations out of loops to reduce per-iteration work.";
        public bool IsEnabledByDefault => true;
        public int Priority => 105; // After constant folding, before CSE

        /// <summary>
        /// Pure methods that can be safely hoisted.
        /// </summary>
        private static readonly HashSet<string> PureMethods = new HashSet<string>
        {
            // Mathf
            "Abs", "Acos", "Asin", "Atan", "Atan2", "Ceil", "CeilToInt", "Clamp", "Clamp01",
            "Cos", "Exp", "Floor", "FloorToInt", "Lerp", "LerpUnclamped", "Log", "Log10",
            "Max", "Min", "Pow", "Round", "RoundToInt", "Sign", "Sin", "Sqrt", "Tan",
            "InverseLerp", "SmoothStep",
            
            // Vector methods
            "Angle", "Cross", "Distance", "Dot", "Lerp", "LerpUnclamped",
            "Max", "Min", "Normalize", "Project", "ProjectOnPlane", "Reflect", "Scale",
            "Slerp", "SlerpUnclamped",
            
            // Quaternion methods
            "Euler", "Inverse",
        };

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new LICMRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private class LICMRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private int _tempVarCounter;
            public bool ChangesMade { get; private set; }

            public LICMRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitForStatement(ForStatementSyntax node)
            {
                // First visit children
                var visited = (ForStatementSyntax)base.VisitForStatement(node);

                // Analyze and optimize
                var result = OptimizeLoop(visited, visited.Statement);
                if (result != null)
                {
                    return result;
                }

                return visited;
            }

            public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node)
            {
                // First visit children
                var visited = (WhileStatementSyntax)base.VisitWhileStatement(node);

                // Analyze and optimize
                var result = OptimizeLoop(visited, visited.Statement);
                if (result != null)
                {
                    return result;
                }

                return visited;
            }

            public override SyntaxNode VisitForEachStatement(ForEachStatementSyntax node)
            {
                // First visit children
                var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node);

                // Analyze and optimize
                var result = OptimizeLoop(visited, visited.Statement);
                if (result != null)
                {
                    return result;
                }

                return visited;
            }

            private SyntaxNode OptimizeLoop(StatementSyntax loopNode, StatementSyntax body)
            {
                if (body == null)
                    return null;

                // Get the loop variable (for 'for' loops)
                var loopVariables = GetLoopVariables(loopNode);

                // Collect all variables modified within the loop body
                var modifiedVariables = CollectModifiedVariables(body);
                modifiedVariables.UnionWith(loopVariables);

                // Find invariant expressions in the loop body
                var invariantExpressions = FindInvariantExpressions(body, modifiedVariables);

                if (invariantExpressions.Count == 0)
                    return null;

                // Create hoisted declarations and replacements
                var hoistedDeclarations = new List<StatementSyntax>();
                var replacements = new Dictionary<string, string>();

                foreach (var expr in invariantExpressions)
                {
                    string tempName = $"__licm_{_tempVarCounter++}";
                    var type = InferType(expr);

                    var declaration = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(type)
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(tempName)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(expr)))));

                    hoistedDeclarations.Add(declaration);
                    replacements[expr.NormalizeWhitespace().ToFullString()] = tempName;

                    _context.RecordOptimization(
                        "CEOPT010",
                        $"Hoisted loop-invariant expression: {TruncateString(expr.ToString(), 50)}",
                        expr.GetLocation(),
                        expr.ToString(),
                        $"Hoisted to {tempName} before loop");

                    ChangesMade = true;
                }

                // Rewrite the loop body with replacements
                var bodyRewriter = new InvariantReplacer(replacements);
                var newBody = (StatementSyntax)bodyRewriter.Visit(body);

                // Create the new loop with the rewritten body
                StatementSyntax newLoop = loopNode switch
                {
                    ForStatementSyntax forLoop => forLoop.WithStatement(newBody),
                    WhileStatementSyntax whileLoop => whileLoop.WithStatement(newBody),
                    ForEachStatementSyntax foreachLoop => foreachLoop.WithStatement(newBody),
                    _ => loopNode
                };

                // Combine hoisted declarations with the loop
                var statements = hoistedDeclarations.Append(newLoop);
                return SyntaxFactory.Block(statements).WithTriviaFrom(loopNode);
            }

            private HashSet<string> GetLoopVariables(StatementSyntax loopNode)
            {
                var variables = new HashSet<string>();

                switch (loopNode)
                {
                    case ForStatementSyntax forLoop:
                        if (forLoop.Declaration != null)
                        {
                            foreach (var variable in forLoop.Declaration.Variables)
                            {
                                variables.Add(variable.Identifier.Text);
                            }
                        }
                        break;

                    case ForEachStatementSyntax foreachLoop:
                        variables.Add(foreachLoop.Identifier.Text);
                        break;
                }

                return variables;
            }

            private HashSet<string> CollectModifiedVariables(StatementSyntax body)
            {
                var collector = new ModifiedVariableCollector();
                collector.Visit(body);
                return collector.ModifiedVariables;
            }

            private List<ExpressionSyntax> FindInvariantExpressions(StatementSyntax body, HashSet<string> modifiedVariables)
            {
                var finder = new InvariantExpressionFinder(modifiedVariables);
                finder.Visit(body);
                
                // Deduplicate by fingerprint
                var seen = new HashSet<string>();
                var result = new List<ExpressionSyntax>();
                
                foreach (var expr in finder.InvariantExpressions)
                {
                    string fingerprint = expr.NormalizeWhitespace().ToFullString();
                    if (seen.Add(fingerprint))
                    {
                        result.Add(expr);
                    }
                }

                return result;
            }

            private TypeSyntax InferType(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    case InvocationExpressionSyntax invocation:
                        return InferTypeFromInvocation(invocation);

                    case MemberAccessExpressionSyntax memberAccess:
                        return InferTypeFromMemberAccess(memberAccess);

                    case BinaryExpressionSyntax _:
                        return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));

                    default:
                        return SyntaxFactory.IdentifierName("var");
                }
            }

            private TypeSyntax InferTypeFromInvocation(InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    string methodName = memberAccess.Name.Identifier.Text;
                    string typeName = memberAccess.Expression.ToString();

                    // Mathf methods
                    if (typeName == "Mathf")
                    {
                        if (methodName == "CeilToInt" || methodName == "FloorToInt" || methodName == "RoundToInt")
                            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword));
                        return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                    }

                    // Vector3 methods
                    if (typeName.Contains("Vector3"))
                    {
                        if (methodName == "Distance" || methodName == "Dot" || methodName == "Angle")
                            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                        return SyntaxFactory.IdentifierName("Vector3");
                    }

                    // Vector2 methods
                    if (typeName.Contains("Vector2"))
                    {
                        if (methodName == "Distance" || methodName == "Dot" || methodName == "Angle")
                            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                        return SyntaxFactory.IdentifierName("Vector2");
                    }

                    // Quaternion methods
                    if (typeName.Contains("Quaternion"))
                    {
                        if (methodName == "Angle" || methodName == "Dot")
                            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                        return SyntaxFactory.IdentifierName("Quaternion");
                    }
                }

                return SyntaxFactory.IdentifierName("var");
            }

            private TypeSyntax InferTypeFromMemberAccess(MemberAccessExpressionSyntax memberAccess)
            {
                string memberName = memberAccess.Name.Identifier.Text;

                switch (memberName)
                {
                    case "position":
                    case "localPosition":
                    case "eulerAngles":
                    case "localEulerAngles":
                    case "lossyScale":
                    case "localScale":
                    case "forward":
                    case "right":
                    case "up":
                        return SyntaxFactory.IdentifierName("Vector3");

                    case "rotation":
                    case "localRotation":
                        return SyntaxFactory.IdentifierName("Quaternion");

                    case "magnitude":
                    case "sqrMagnitude":
                    case "x":
                    case "y":
                    case "z":
                    case "w":
                        return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));

                    default:
                        return SyntaxFactory.IdentifierName("var");
                }
            }

            private string TruncateString(string s, int maxLength)
            {
                if (s.Length <= maxLength)
                    return s;
                return s.Substring(0, maxLength) + "...";
            }
        }

        /// <summary>
        /// Collects all variables that are modified within a code block.
        /// </summary>
        private class ModifiedVariableCollector : CSharpSyntaxWalker
        {
            public HashSet<string> ModifiedVariables { get; } = new HashSet<string>();

            public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
            {
                if (node.Left is IdentifierNameSyntax identifier)
                {
                    ModifiedVariables.Add(identifier.Identifier.Text);
                }
                base.VisitAssignmentExpression(node);
            }

            public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
            {
                if ((node.Kind() == SyntaxKind.PostIncrementExpression || 
                     node.Kind() == SyntaxKind.PostDecrementExpression) &&
                    node.Operand is IdentifierNameSyntax identifier)
                {
                    ModifiedVariables.Add(identifier.Identifier.Text);
                }
                base.VisitPostfixUnaryExpression(node);
            }

            public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
            {
                if ((node.Kind() == SyntaxKind.PreIncrementExpression || 
                     node.Kind() == SyntaxKind.PreDecrementExpression) &&
                    node.Operand is IdentifierNameSyntax identifier)
                {
                    ModifiedVariables.Add(identifier.Identifier.Text);
                }
                base.VisitPrefixUnaryExpression(node);
            }

            public override void VisitArgument(ArgumentSyntax node)
            {
                // Track out/ref parameters
                if (node.RefKindKeyword.Kind() == SyntaxKind.OutKeyword ||
                    node.RefKindKeyword.Kind() == SyntaxKind.RefKeyword)
                {
                    if (node.Expression is IdentifierNameSyntax identifier)
                    {
                        ModifiedVariables.Add(identifier.Identifier.Text);
                    }
                }
                base.VisitArgument(node);
            }

            public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
            {
                // Variables declared inside the loop are not invariant
                foreach (var variable in node.Declaration.Variables)
                {
                    ModifiedVariables.Add(variable.Identifier.Text);
                }
                base.VisitLocalDeclarationStatement(node);
            }
        }

        /// <summary>
        /// Finds expressions that are invariant (don't depend on modified variables).
        /// </summary>
        private class InvariantExpressionFinder : CSharpSyntaxWalker
        {
            private readonly HashSet<string> _modifiedVariables;
            public List<ExpressionSyntax> InvariantExpressions { get; } = new List<ExpressionSyntax>();

            // Track nesting depth to avoid expressions in conditionals
            private int _conditionalDepth;

            public InvariantExpressionFinder(HashSet<string> modifiedVariables)
            {
                _modifiedVariables = modifiedVariables;
            }

            public override void VisitIfStatement(IfStatementSyntax node)
            {
                // Visit condition normally
                Visit(node.Condition);

                // Mark statements as conditional
                _conditionalDepth++;
                Visit(node.Statement);
                if (node.Else != null)
                    Visit(node.Else);
                _conditionalDepth--;
            }

            public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
            {
                // Only visit condition, not branches
                Visit(node.Condition);
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // Only consider expressions not inside conditionals
                if (_conditionalDepth == 0 && IsPureInvocation(node) && IsInvariant(node))
                {
                    InvariantExpressions.Add(node);
                    // Don't recurse into already-captured expressions
                    return;
                }

                base.VisitInvocationExpression(node);
            }

            private bool IsPureInvocation(InvocationExpressionSyntax node)
            {
                if (node.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    string methodName = memberAccess.Name.Identifier.Text;
                    return PureMethods.Contains(methodName);
                }
                return false;
            }

            private bool IsInvariant(ExpressionSyntax expression)
            {
                var identifiers = expression.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>()
                    .Select(id => id.Identifier.Text);

                foreach (var identifier in identifiers)
                {
                    // Modified variables include those declared inside the loop body,
                    // so any reference to them makes the expression non-invariant
                    if (_modifiedVariables.Contains(identifier))
                        return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Replaces invariant expressions with cached variable references.
        /// </summary>
        private class InvariantReplacer : CSharpSyntaxRewriter
        {
            private readonly Dictionary<string, string> _replacements;

            public InvariantReplacer(Dictionary<string, string> replacements)
            {
                _replacements = replacements;
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                string fingerprint = node.NormalizeWhitespace().ToFullString();
                if (_replacements.TryGetValue(fingerprint, out var tempName))
                {
                    return SyntaxFactory.IdentifierName(tempName).WithTriviaFrom(node);
                }

                return base.VisitInvocationExpression(node);
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                string fingerprint = node.NormalizeWhitespace().ToFullString();
                if (_replacements.TryGetValue(fingerprint, out var tempName))
                {
                    return SyntaxFactory.IdentifierName(tempName).WithTriviaFrom(node);
                }

                return base.VisitMemberAccessExpression(node);
            }
        }
    }
}


