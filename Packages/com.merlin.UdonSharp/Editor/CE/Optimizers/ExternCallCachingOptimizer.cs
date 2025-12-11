using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Detects patterns where the same extern property is accessed multiple times
    /// for its components and consolidates to a single call.
    /// 
    /// This is particularly valuable for Unity properties that are extern calls in Udon,
    /// such as transform.position which becomes an expensive VM call.
    /// 
    /// Examples:
    /// - transform.position.x, transform.position.y, transform.position.z → cache position once
    /// - GetComponent&lt;T&gt;() called multiple times → cache the result
    /// - Repeated access to same VRChat API properties → cache
    /// </summary>
    internal class ExternCallCachingOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT008";
        public string OptimizerName => "Extern Call Caching";
        public string Description => "Caches repeated extern property accesses to reduce expensive VM calls.";
        public bool IsEnabledByDefault => true;
        public int Priority => 120; // After CSE to catch remaining patterns

        /// <summary>
        /// Properties that are expensive extern calls worth caching.
        /// Maps property name to expected return type.
        /// </summary>
        private static readonly Dictionary<string, string> ExpensiveProperties = new Dictionary<string, string>
        {
            // Transform properties
            { "position", "Vector3" },
            { "localPosition", "Vector3" },
            { "rotation", "Quaternion" },
            { "localRotation", "Quaternion" },
            { "eulerAngles", "Vector3" },
            { "localEulerAngles", "Vector3" },
            { "lossyScale", "Vector3" },
            { "localScale", "Vector3" },
            { "forward", "Vector3" },
            { "right", "Vector3" },
            { "up", "Vector3" },
            
            // Rigidbody properties
            { "velocity", "Vector3" },
            { "angularVelocity", "Vector3" },
            
            // Camera properties
            { "worldToCameraMatrix", "Matrix4x4" },
            { "cameraToWorldMatrix", "Matrix4x4" },
            { "projectionMatrix", "Matrix4x4" },
        };

        /// <summary>
        /// Component access patterns (type name patterns).
        /// </summary>
        private static readonly HashSet<string> ComponentAccessPatterns = new HashSet<string>
        {
            "transform",
            "gameObject",
            "rigidbody",
        };

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new ExternCacheRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private class ExternCacheRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private int _cacheVarCounter;
            public bool ChangesMade { get; private set; }

            public ExternCacheRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
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
                // Collect all member access expressions that access expensive properties
                var collector = new ExternAccessCollector();
                collector.Visit(block);

                // Group by base expression to find patterns like:
                // transform.position.x, transform.position.y, transform.position.z
                var accessGroups = collector.Accesses
                    .GroupBy(a => a.BaseExpression)
                    .Where(g => g.Count() >= 2) // Must have at least 2 accesses to benefit
                    .ToList();

                if (accessGroups.Count == 0)
                    return block;

                // Create cache variables for each group
                var cacheDeclarations = new List<StatementSyntax>();
                var replacements = new Dictionary<string, string>();

                foreach (var group in accessGroups)
                {
                    var first = group.First();
                    string cacheName = $"__cache_{_cacheVarCounter++}";
                    
                    // Get the type for the cached value
                    var type = GetTypeForProperty(first.PropertyName);

                    // Create the cache variable declaration
                    var declaration = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(type)
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(cacheName)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                                        first.FullAccessExpression)))));

                    cacheDeclarations.Add(declaration);

                    // Record the replacement
                    string fingerprint = first.FullAccessExpression.NormalizeWhitespace().ToFullString();
                    replacements[fingerprint] = cacheName;

                    _context.RecordOptimization(
                        "CEOPT008",
                        $"Cached {group.Count()} accesses to {first.BaseExpression}.{first.PropertyName}",
                        first.FullAccessExpression.GetLocation(),
                        $"{first.BaseExpression}.{first.PropertyName} (accessed {group.Count()} times)",
                        $"Cached in {cacheName}");

                    ChangesMade = true;
                }

                if (replacements.Count == 0)
                    return block;

                // Rewrite the block with cached references
                var rewriter = new AccessReplacer(replacements);
                var newBlock = (BlockSyntax)rewriter.Visit(block);

                // Insert cache declarations at the beginning of the block
                var newStatements = cacheDeclarations.Concat(newBlock.Statements);
                return newBlock.WithStatements(SyntaxFactory.List(newStatements));
            }

            private TypeSyntax GetTypeForProperty(string propertyName)
            {
                if (ExpensiveProperties.TryGetValue(propertyName, out var typeName))
                {
                    return SyntaxFactory.IdentifierName(typeName);
                }

                return SyntaxFactory.IdentifierName("var");
            }
        }

        /// <summary>
        /// Information about an expensive property access.
        /// </summary>
        private class ExternAccessInfo
        {
            /// <summary>
            /// The base expression (e.g., "transform" in "transform.position").
            /// </summary>
            public string BaseExpression { get; set; }

            /// <summary>
            /// The property being accessed (e.g., "position").
            /// </summary>
            public string PropertyName { get; set; }

            /// <summary>
            /// The full member access expression.
            /// </summary>
            public MemberAccessExpressionSyntax FullAccessExpression { get; set; }

            /// <summary>
            /// Whether this is a component access like .x, .y, .z.
            /// </summary>
            public bool HasComponentAccess { get; set; }
        }

        /// <summary>
        /// Collects expensive extern property accesses.
        /// </summary>
        private class ExternAccessCollector : CSharpSyntaxWalker
        {
            public List<ExternAccessInfo> Accesses { get; } = new List<ExternAccessInfo>();

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // Check if this is a component access pattern like transform.position.x
                if (IsExpensivePropertyAccess(node, out var info))
                {
                    Accesses.Add(info);
                }

                base.VisitMemberAccessExpression(node);
            }

            private bool IsExpensivePropertyAccess(MemberAccessExpressionSyntax node, out ExternAccessInfo info)
            {
                info = null;

                string propertyName = node.Name.Identifier.Text;

                // Check if this property is expensive
                if (!ExpensiveProperties.ContainsKey(propertyName))
                    return false;

                // Get the base expression
                string baseExpr = GetBaseExpression(node.Expression);
                if (string.IsNullOrEmpty(baseExpr))
                    return false;

                // Only cache if it's on a known component accessor
                if (!IsComponentAccessor(node.Expression))
                    return false;

                info = new ExternAccessInfo
                {
                    BaseExpression = baseExpr,
                    PropertyName = propertyName,
                    FullAccessExpression = node,
                    HasComponentAccess = false
                };

                return true;
            }

            private string GetBaseExpression(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.Text;

                    case MemberAccessExpressionSyntax memberAccess:
                        // e.g., this.transform or gameObject.transform
                        return memberAccess.ToString();

                    case ThisExpressionSyntax _:
                        return "this";

                    default:
                        return expr.ToString();
                }
            }

            private bool IsComponentAccessor(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case IdentifierNameSyntax identifier:
                        return ComponentAccessPatterns.Contains(identifier.Identifier.Text);

                    case MemberAccessExpressionSyntax memberAccess:
                        return ComponentAccessPatterns.Contains(memberAccess.Name.Identifier.Text);

                    case ThisExpressionSyntax _:
                        return true; // this.position is valid

                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Replaces expensive property accesses with cached variable references.
        /// </summary>
        private class AccessReplacer : CSharpSyntaxRewriter
        {
            private readonly Dictionary<string, string> _replacements;
            private readonly HashSet<string> _seenFirst;

            public AccessReplacer(Dictionary<string, string> replacements)
            {
                _replacements = replacements;
                _seenFirst = new HashSet<string>();
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                string fingerprint = node.NormalizeWhitespace().ToFullString();

                if (_replacements.TryGetValue(fingerprint, out var cacheName))
                {
                    // Skip the first occurrence - it's used in the declaration
                    if (_seenFirst.Add(fingerprint))
                    {
                        return base.VisitMemberAccessExpression(node);
                    }

                    // Replace with the cached variable
                    return SyntaxFactory.IdentifierName(cacheName).WithTriviaFrom(node);
                }

                return base.VisitMemberAccessExpression(node);
            }
        }
    }
}


