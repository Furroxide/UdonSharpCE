using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Transforms CELogger static method calls into UnityEngine.Debug calls at compile time.
    /// 
    /// This optimizer enables UdonSharp scripts to use the CELogger API, which would
    /// otherwise fail compilation because Udon cannot call static methods on user-defined types.
    /// 
    /// Transformations:
    /// - CELogger.Info(msg) → Debug.Log("[CE][Info] " + msg)
    /// - CELogger.Info(tag, msg) → Debug.Log("[CE][tag][Info] " + msg)
    /// - CELogger.Warning(msg) → Debug.LogWarning("[CE][Warning] " + msg)
    /// - CELogger.Error(msg) → Debug.LogError("[CE][Error] " + msg)
    /// - etc.
    /// </summary>
    internal class CELoggerTransformOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT006";
        public string OptimizerName => "CELogger Transform";
        public string Description => "Transforms CELogger calls into Udon-compatible Debug.Log calls.";
        public bool IsEnabledByDefault => true;
        public int Priority => 5; // Very early - run before other optimizations

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new CELoggerRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private class CELoggerRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            public bool ChangesMade { get; private set; }

            public CELoggerRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // First visit children
                var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);

                // Check if this is a CELogger call
                if (visited.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    string typeName = GetTypeName(memberAccess.Expression);
                    string methodName = memberAccess.Name.Identifier.ValueText;

                    // Match CELogger or full namespace
                    if (typeName == "CELogger" || 
                        typeName == "UdonSharp.CE.DevTools.CELogger")
                    {
                        var transformed = TransformCELoggerCall(visited, methodName);
                        if (transformed != null)
                        {
                            var originalText = node.ToString();
                            var optimizedText = transformed.ToString();

                            _context.RecordOptimization(
                                "CEOPT006",
                                $"Transformed CELogger call: {methodName}",
                                node.GetLocation(),
                                originalText,
                                optimizedText);

                            ChangesMade = true;
                            return transformed.WithTriviaFrom(node);
                        }
                    }
                }

                return visited;
            }

            private string GetTypeName(ExpressionSyntax expression)
            {
                return expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    QualifiedNameSyntax qualified => qualified.ToString(),
                    MemberAccessExpressionSyntax memberAccess => memberAccess.ToString(),
                    _ => ""
                };
            }

            private InvocationExpressionSyntax TransformCELoggerCall(
                InvocationExpressionSyntax node, 
                string methodName)
            {
                var args = node.ArgumentList.Arguments;

                // Determine the Debug method and log level
                string debugMethod;
                string levelTag;

                switch (methodName)
                {
                    case "Trace":
                        debugMethod = "Log";
                        levelTag = "Trace";
                        break;
                    case "Debug":
                        debugMethod = "Log";
                        levelTag = "Debug";
                        break;
                    case "Info":
                        debugMethod = "Log";
                        levelTag = "Info";
                        break;
                    case "Warning":
                        debugMethod = "LogWarning";
                        levelTag = "Warning";
                        break;
                    case "Error":
                        debugMethod = "LogError";
                        levelTag = "Error";
                        break;
                    case "Log":
                        // Handle Log(message, level) and Log(tag, message, level)
                        return TransformLogCall(node, args);
                    default:
                        // Unsupported method, return null to skip transformation
                        return null;
                }

                // Create the transformed Debug call
                return CreateDebugCall(debugMethod, levelTag, args);
            }

            private InvocationExpressionSyntax TransformLogCall(
                InvocationExpressionSyntax node,
                SeparatedSyntaxList<ArgumentSyntax> args)
            {
                // Log has two overloads:
                // Log(string message, LogLevel level = LogLevel.Info)
                // Log(string tag, string message, LogLevel level)
                
                if (args.Count < 1)
                    return null;

                // For simplicity, transform to Debug.Log with the message
                // This loses the level filtering, but at least the code compiles
                
                ExpressionSyntax messageExpr;
                string prefix;

                if (args.Count >= 3)
                {
                    // Log(tag, message, level) format
                    var tagExpr = args[0].Expression;
                    messageExpr = args[1].Expression;
                    prefix = "[CE][\" + ";
                    
                    // Build: "[CE][" + tag + "] " + message
                    var prefixExpr = CreateStringConcat(
                        CreateStringConcat(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal("[CE][")),
                            tagExpr),
                        CreateStringConcat(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal("] ")),
                            messageExpr));
                    
                    return CreateDebugLogCall("Log", prefixExpr);
                }
                else
                {
                    // Log(message, level?) format
                    messageExpr = args[0].Expression;
                    prefix = "[CE] ";
                    
                    var prefixedMessage = CreateStringConcat(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(prefix)),
                        messageExpr);
                    
                    return CreateDebugLogCall("Log", prefixedMessage);
                }
            }

            private InvocationExpressionSyntax CreateDebugCall(
                string debugMethod, 
                string levelTag,
                SeparatedSyntaxList<ArgumentSyntax> args)
            {
                if (args.Count < 1)
                    return null;

                ExpressionSyntax messageExpr;

                if (args.Count == 1)
                {
                    // Single argument: method(message)
                    // Transform to: Debug.Log("[CE][Level] " + message)
                    messageExpr = args[0].Expression;
                    string prefix = $"[CE][{levelTag}] ";
                    
                    var prefixedMessage = CreateStringConcat(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(prefix)),
                        messageExpr);
                    
                    return CreateDebugLogCall(debugMethod, prefixedMessage);
                }
                else if (args.Count == 2)
                {
                    // Two arguments: method(tag, message)
                    // Transform to: Debug.Log("[CE][tag][Level] " + message)
                    var tagExpr = args[0].Expression;
                    messageExpr = args[1].Expression;

                    // Build: "[CE][" + tag + "][Level] " + message
                    var prefixExpr = CreateStringConcat(
                        CreateStringConcat(
                            CreateStringConcat(
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.StringLiteralExpression,
                                    SyntaxFactory.Literal("[CE][")),
                                tagExpr),
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal($"][{levelTag}] "))),
                        messageExpr);

                    return CreateDebugLogCall(debugMethod, prefixExpr);
                }

                return null;
            }

            private BinaryExpressionSyntax CreateStringConcat(
                ExpressionSyntax left, 
                ExpressionSyntax right)
            {
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.AddExpression,
                    left,
                    right);
            }

            private InvocationExpressionSyntax CreateDebugLogCall(
                string methodName, 
                ExpressionSyntax argument)
            {
                // Create: UnityEngine.Debug.Log(argument)
                // Using full namespace to avoid ambiguity
                var debugAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("UnityEngine"),
                        SyntaxFactory.IdentifierName("Debug")),
                    SyntaxFactory.IdentifierName(methodName));

                return SyntaxFactory.InvocationExpression(
                    debugAccess,
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(argument))));
            }
        }
    }
}


























