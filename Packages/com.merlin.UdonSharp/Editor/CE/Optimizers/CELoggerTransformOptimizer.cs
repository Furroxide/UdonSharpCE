using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Transforms CELogger static method calls into UnityEngine.Debug calls at compile time.
    /// 
    /// UdonSharp can compile a real call into CELogger, but CELogger's static state (MinLevel,
    /// OutputToUnityLog, registered consoles) lives in a separate copy inside every UdonBehaviour that
    /// uses it, so filtering and console routing configured in one behaviour never reach another. Writing
    /// each call as the Debug call it amounts to gives every behaviour the same, predictable output
    /// without carrying CELogger's static initializer into its program.
    /// 
    /// Transformations:
    /// - CELogger.Info(msg) → Debug.Log("[CE][Info] " + msg)
    /// - CELogger.Info(tag, msg) → Debug.Log("[CE][tag][Info] " + msg)
    /// - CELogger.Warning(msg) → Debug.LogWarning("[CE][Warning] " + msg)
    /// - CELogger.Error(msg) → Debug.LogError("[CE][Error] " + msg)
    /// - CELogger.Log(msg, LogLevel.X) and CELogger.Log(tag, msg, LogLevel.X) → routed by X exactly as the
    ///   named method for that level is
    ///
    /// A call is left as written, as a real call into CELogger, when its level is not a compile-time
    /// constant (the Debug method cannot be chosen) or when it names its arguments (the concatenation
    /// would evaluate them in parameter order rather than the order they were written).
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
            var rewriter = new CELoggerRewriter(context, context.GetSemanticModel(tree));
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
            private readonly SemanticModel _semanticModel;
            public bool ChangesMade { get; private set; }

            public CELoggerRewriter(OptimizationContext context, SemanticModel semanticModel)
            {
                _context = context;
                _semanticModel = semanticModel;
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // First visit children
                var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);

                var method = _semanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol;
                if (method?.ContainingType.ToDisplayString() != "UdonSharp.CE.DevTools.CELogger")
                    return visited;

                // Check if this is a CELogger call
                if (visited.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    string typeName = GetTypeName(memberAccess.Expression);
                    string methodName = memberAccess.Name.Identifier.ValueText;

                    // Match CELogger or full namespace
                    if (typeName == "CELogger" || 
                        typeName == "UdonSharp.CE.DevTools.CELogger")
                    {
                        var transformed = TransformCELoggerCall(node, methodName);
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
                            var replacement = transformed.WithTriviaFrom(node);
                            int droppedLines = node.ToFullString().Count(c => c == '\n') - replacement.ToFullString().Count(c => c == '\n');
                            if (droppedLines > 0)
                                replacement = replacement.WithTrailingTrivia(replacement.GetTrailingTrivia().AddRange(
                                    Enumerable.Repeat(SyntaxFactory.EndOfLine("\n"), droppedLines)));
                            return replacement;
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
                // Leaving a named call standing preserves its written argument evaluation order.
                if (args.Any(argument => argument.NameColon != null)) return null;

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
                if (args.Count < 1 || args.Count > 3) return null;
                string level = "Info";
                int messageArguments = args.Count == 3 ? 2 : 1;
                if (args.Count > 1)
                {
                    var constant = _semanticModel.GetConstantValue(node.ArgumentList.Arguments[args.Count - 1].Expression);
                    if (!constant.HasValue || !(constant.Value is int value) || value < 0 || value > 4) return null;
                    level = new[] { "Trace", "Debug", "Info", "Warning", "Error" }[value];
                }
                string debug = level == "Warning" ? "LogWarning" : level == "Error" ? "LogError" : "Log";
                return CreateDebugCall(debug, level, SyntaxFactory.SeparatedList(args.Take(messageArguments)));
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
                    messageExpr = AsOperand(args[0].Expression);
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
                    var tagExpr = AsOperand(args[0].Expression);
                    messageExpr = AsOperand(args[1].Expression);

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

            /// <summary>
            /// The concatenation is assembled as a tree, so it binds as intended whatever the operand is.
            /// Its text does not. Printed bare, <c>Info(count + 1 + " left")</c> reads -- and re-parses -- as
            /// <c>"[CE][Info] " + count + 1 + " left"</c>, which concatenates from the left and logs "41 left"
            /// for a count of 4; <c>a ?? b</c> turns into <c>("[CE][Info] " + a) ?? b</c>, which is never
            /// null and so never falls back to b; and a conditional's condition swallows the prefix. The
            /// text is what the optimization report shows and what U# publishes as the module's source, so
            /// an operand that is not already a primary expression is parenthesized to keep the two saying
            /// the same thing.
            /// </summary>
            private static ExpressionSyntax AsOperand(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    case LiteralExpressionSyntax _:
                    case IdentifierNameSyntax _:
                    case MemberAccessExpressionSyntax _:
                    case InvocationExpressionSyntax _:
                    case ElementAccessExpressionSyntax _:
                    case ParenthesizedExpressionSyntax _:
                    case InterpolatedStringExpressionSyntax _:
                        return expression;
                    default:
                        return SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia())
                            .WithTriviaFrom(expression);
                }
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
