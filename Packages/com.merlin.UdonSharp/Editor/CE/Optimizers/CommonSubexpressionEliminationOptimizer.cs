using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Detects repeated pure expressions within the same block scope and caches them
    /// in a temporary variable to avoid redundant computation.
    /// 
    /// This optimizer identifies expressions that:
    /// - Appear 2+ times within the same block scope
    /// - Are pure (no side effects)
    /// - Have the same normalized form
    /// 
    /// Examples:
    /// - Vector3.Distance(a, b) called twice → cached in temp variable
    /// - transform.position accessed multiple times → cached
    /// - Complex arithmetic expressions repeated → cached
    /// </summary>
    internal class CommonSubexpressionEliminationOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT006";
        public string OptimizerName => "Common Subexpression Elimination";
        public string Description => "Caches repeated pure expressions to avoid redundant computation.";
        public bool IsEnabledByDefault => true;
        public int Priority => 115; // After dead code elimination, before loop unrolling

        /// <summary>
        /// Known pure static methods that can be safely cached.
        /// </summary>
        private static readonly HashSet<string> PureStaticMethods = new HashSet<string>
        {
            // Mathf methods
            "Mathf.Abs", "Mathf.Acos", "Mathf.Asin", "Mathf.Atan", "Mathf.Atan2",
            "Mathf.Ceil", "Mathf.CeilToInt", "Mathf.Clamp", "Mathf.Clamp01",
            "Mathf.Cos", "Mathf.Exp", "Mathf.Floor", "Mathf.FloorToInt",
            "Mathf.Lerp", "Mathf.LerpUnclamped", "Mathf.Log", "Mathf.Log10",
            "Mathf.Max", "Mathf.Min", "Mathf.Pow", "Mathf.Round", "Mathf.RoundToInt",
            "Mathf.Sign", "Mathf.Sin", "Mathf.Sqrt", "Mathf.Tan",
            "Mathf.InverseLerp", "Mathf.SmoothStep",
            
            // Vector methods
            "Vector2.Angle", "Vector2.Distance", "Vector2.Dot", "Vector2.Lerp",
            "Vector2.Max", "Vector2.Min", "Vector2.Scale",
            "Vector3.Angle", "Vector3.Cross", "Vector3.Distance", "Vector3.Dot",
            "Vector3.Lerp", "Vector3.LerpUnclamped", "Vector3.Max", "Vector3.Min",
            "Vector3.Normalize", "Vector3.Project", "Vector3.ProjectOnPlane",
            "Vector3.Reflect", "Vector3.Scale", "Vector3.Slerp", "Vector3.SlerpUnclamped",
            "Vector4.Distance", "Vector4.Dot", "Vector4.Lerp", "Vector4.Max",
            "Vector4.Min", "Vector4.Normalize", "Vector4.Scale",
            
            // Quaternion methods
            "Quaternion.Angle", "Quaternion.Dot", "Quaternion.Euler",
            "Quaternion.Inverse", "Quaternion.Lerp", "Quaternion.LerpUnclamped",
            "Quaternion.Slerp", "Quaternion.SlerpUnclamped",
            
            // Color methods
            "Color.Lerp", "Color.LerpUnclamped",
        };

        /// <summary>
        /// Known pure instance methods that can be safely cached (method name only).
        /// </summary>
        private static readonly HashSet<string> PureInstanceMethods = new HashSet<string>
        {
            // Vector normalized properties are actually computed
            "normalized", "magnitude", "sqrMagnitude",
        };

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new CSERewriter(context, this);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private class CSERewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly CommonSubexpressionEliminationOptimizer _optimizer;
            private int _tempVarCounter;
            public bool ChangesMade { get; private set; }

            public CSERewriter(OptimizationContext context, CommonSubexpressionEliminationOptimizer optimizer)
            {
                _context = context;
                _optimizer = optimizer;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                // Process each method body independently
                if (node.Body != null)
                {
                    var optimizedBody = OptimizeBlock(node.Body);
                    if (optimizedBody != node.Body)
                    {
                        return node.WithBody(optimizedBody);
                    }
                }

                return base.VisitMethodDeclaration(node);
            }

            public override SyntaxNode VisitAccessorDeclaration(AccessorDeclarationSyntax node)
            {
                // Process property accessor bodies
                if (node.Body != null)
                {
                    var optimizedBody = OptimizeBlock(node.Body);
                    if (optimizedBody != node.Body)
                    {
                        return node.WithBody(optimizedBody);
                    }
                }

                return base.VisitAccessorDeclaration(node);
            }

            private BlockSyntax OptimizeBlock(BlockSyntax block)
            {
                // Collect all expressions and their fingerprints
                var expressionCollector = new ExpressionCollector();
                expressionCollector.Visit(block);

                // Find expressions that appear 2+ times and are pure
                var duplicates = expressionCollector.Expressions
                    .GroupBy(e => e.Fingerprint)
                    .Where(g => g.Count() >= 2 && g.First().IsPure)
                    .ToList();

                if (duplicates.Count == 0)
                    return block;

                // Create replacement map
                var replacements = new Dictionary<string, (string tempName, ExpressionSyntax firstExpr, TypeSyntax type)>();
                foreach (var group in duplicates)
                {
                    var first = group.First();
                    string tempName = $"__cse_{_tempVarCounter++}";
                    var type = InferType(first.Expression);
                    if (type != null)
                    {
                        replacements[group.Key] = (tempName, first.Expression, type);
                    }
                }

                if (replacements.Count == 0)
                    return block;

                // Rewrite the block with cached expressions
                var rewriter = new ExpressionReplacer(replacements);
                var newBlock = (BlockSyntax)rewriter.Visit(block);

                // Insert variable declarations at the start of the block
                var declarations = new List<StatementSyntax>();
                foreach (var kvp in replacements)
                {
                    var (tempName, expr, type) = kvp.Value;
                    
                    var declaration = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(type)
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(tempName)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(expr)))));

                    declarations.Add(declaration);

                    _context.RecordOptimization(
                        "CEOPT006",
                        $"Cached repeated expression: {expr.ToString().Substring(0, Math.Min(50, expr.ToString().Length))}...",
                        expr.GetLocation(),
                        expr.ToString(),
                        tempName);

                    ChangesMade = true;
                }

                // Combine declarations with the rewritten statements
                var newStatements = declarations.Concat(newBlock.Statements);
                return newBlock.WithStatements(SyntaxFactory.List(newStatements));
            }

            private TypeSyntax InferType(ExpressionSyntax expression)
            {
                // Infer type from the expression structure
                switch (expression)
                {
                    case InvocationExpressionSyntax invocation:
                        return InferTypeFromInvocation(invocation);

                    case MemberAccessExpressionSyntax memberAccess:
                        return InferTypeFromMemberAccess(memberAccess);

                    case BinaryExpressionSyntax binary:
                        // For arithmetic, assume same type as operands or float
                        return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));

                    default:
                        // Use var for unknown types
                        return SyntaxFactory.IdentifierName("var");
                }
            }

            private TypeSyntax InferTypeFromInvocation(InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    string methodName = memberAccess.Name.Identifier.Text;
                    string typeName = memberAccess.Expression.ToString();

                    // Vector methods
                    if (typeName.StartsWith("Vector3"))
                    {
                        if (methodName == "Distance" || methodName == "Dot" || methodName == "Angle")
                            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                        if (methodName == "Cross" || methodName == "Normalize" || methodName == "Lerp" || 
                            methodName == "Project" || methodName == "Reflect" || methodName == "Scale")
                            return SyntaxFactory.IdentifierName("Vector3");
                    }
                    if (typeName.StartsWith("Vector2"))
                    {
                        if (methodName == "Distance" || methodName == "Dot" || methodName == "Angle")
                            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                        return SyntaxFactory.IdentifierName("Vector2");
                    }

                    // Mathf methods
                    if (typeName == "Mathf")
                    {
                        if (methodName == "CeilToInt" || methodName == "FloorToInt" || methodName == "RoundToInt")
                            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword));
                        return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword));
                    }

                    // Quaternion methods
                    if (typeName.StartsWith("Quaternion"))
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

                // Common property types
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

                    case "normalized":
                        // Could be Vector2 or Vector3 - use var
                        return SyntaxFactory.IdentifierName("var");

                    default:
                        return SyntaxFactory.IdentifierName("var");
                }
            }
        }

        /// <summary>
        /// Collects expressions and their normalized fingerprints from a syntax tree.
        /// </summary>
        private class ExpressionCollector : CSharpSyntaxWalker
        {
            public List<ExpressionInfo> Expressions { get; } = new List<ExpressionInfo>();

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // Check if this is a pure method call
                if (IsPureInvocation(node))
                {
                    string fingerprint = NormalizeExpression(node);
                    Expressions.Add(new ExpressionInfo
                    {
                        Expression = node,
                        Fingerprint = fingerprint,
                        IsPure = true
                    });
                }

                base.VisitInvocationExpression(node);
            }

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // Only consider member access expressions that are used as values
                // and are potentially expensive (like transform.position)
                if (IsPureMemberAccess(node) && IsExpensiveMemberAccess(node))
                {
                    string fingerprint = NormalizeExpression(node);
                    Expressions.Add(new ExpressionInfo
                    {
                        Expression = node,
                        Fingerprint = fingerprint,
                        IsPure = true
                    });
                }

                base.VisitMemberAccessExpression(node);
            }

            private bool IsPureInvocation(InvocationExpressionSyntax node)
            {
                if (node.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    string fullName = $"{memberAccess.Expression}.{memberAccess.Name.Identifier.Text}";
                    
                    // Check against known pure static methods
                    if (PureStaticMethods.Contains(fullName))
                        return true;

                    // Check for type-qualified calls like Vector3.Distance
                    string typeDotMethod = $"{memberAccess.Expression}.{memberAccess.Name.Identifier.Text}";
                    foreach (var pureMethod in PureStaticMethods)
                    {
                        if (typeDotMethod.EndsWith(pureMethod.Split('.').Last()) && 
                            typeDotMethod.Contains(pureMethod.Split('.').First()))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private bool IsPureMemberAccess(MemberAccessExpressionSyntax node)
            {
                // Property accesses on transforms, etc. are generally pure reads
                string memberName = node.Name.Identifier.Text;

                // These are pure property reads
                var pureProperties = new HashSet<string>
                {
                    "position", "localPosition", "rotation", "localRotation",
                    "eulerAngles", "localEulerAngles", "lossyScale", "localScale",
                    "forward", "right", "up", "magnitude", "sqrMagnitude", "normalized",
                    "x", "y", "z", "w"
                };

                return pureProperties.Contains(memberName);
            }

            private bool IsExpensiveMemberAccess(MemberAccessExpressionSyntax node)
            {
                // These are expensive (extern calls in Udon)
                string memberName = node.Name.Identifier.Text;

                var expensiveProperties = new HashSet<string>
                {
                    "position", "localPosition", "rotation", "localRotation",
                    "eulerAngles", "localEulerAngles", "lossyScale", "localScale",
                    "forward", "right", "up", "normalized"
                };

                // Also check if the receiver is transform, gameObject, etc.
                if (node.Expression is IdentifierNameSyntax identifier)
                {
                    string receiverName = identifier.Identifier.Text;
                    if (receiverName == "transform" || receiverName == "gameObject")
                    {
                        return expensiveProperties.Contains(memberName);
                    }
                }
                else if (node.Expression is MemberAccessExpressionSyntax innerAccess)
                {
                    string innerMember = innerAccess.Name.Identifier.Text;
                    if (innerMember == "transform" || innerMember == "gameObject")
                    {
                        return expensiveProperties.Contains(memberName);
                    }
                }

                return false;
            }

            private string NormalizeExpression(ExpressionSyntax expr)
            {
                // Remove whitespace and normalize to create a fingerprint
                return expr.NormalizeWhitespace().ToFullString();
            }
        }

        private class ExpressionInfo
        {
            public ExpressionSyntax Expression { get; set; }
            public string Fingerprint { get; set; }
            public bool IsPure { get; set; }
        }

        /// <summary>
        /// Replaces expressions with cached variable references.
        /// </summary>
        private class ExpressionReplacer : CSharpSyntaxRewriter
        {
            private readonly Dictionary<string, (string tempName, ExpressionSyntax firstExpr, TypeSyntax type)> _replacements;
            private readonly HashSet<ExpressionSyntax> _firstOccurrences;
            private readonly Dictionary<string, bool> _seenFirst;

            public ExpressionReplacer(Dictionary<string, (string tempName, ExpressionSyntax firstExpr, TypeSyntax type)> replacements)
            {
                _replacements = replacements;
                _firstOccurrences = new HashSet<ExpressionSyntax>(replacements.Values.Select(v => v.firstExpr));
                _seenFirst = new Dictionary<string, bool>();
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                string fingerprint = node.NormalizeWhitespace().ToFullString();
                if (_replacements.TryGetValue(fingerprint, out var replacement))
                {
                    // Skip the first occurrence - it will be in the declaration
                    if (!_seenFirst.ContainsKey(fingerprint))
                    {
                        _seenFirst[fingerprint] = true;
                        return base.VisitInvocationExpression(node);
                    }

                    // Replace with the temp variable reference
                    return SyntaxFactory.IdentifierName(replacement.tempName)
                        .WithTriviaFrom(node);
                }

                return base.VisitInvocationExpression(node);
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                string fingerprint = node.NormalizeWhitespace().ToFullString();
                if (_replacements.TryGetValue(fingerprint, out var replacement))
                {
                    // Skip the first occurrence - it will be in the declaration
                    if (!_seenFirst.ContainsKey(fingerprint))
                    {
                        _seenFirst[fingerprint] = true;
                        return base.VisitMemberAccessExpression(node);
                    }

                    // Replace with the temp variable reference
                    return SyntaxFactory.IdentifierName(replacement.tempName)
                        .WithTriviaFrom(node);
                }

                return base.VisitMemberAccessExpression(node);
            }
        }
    }
}


