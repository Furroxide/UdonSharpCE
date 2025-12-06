using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Async
{
    /// <summary>
    /// Types of await expressions that can be bound.
    /// </summary>
    public enum AwaitType
    {
        /// <summary>
        /// await UdonTask.Delay(seconds) - time-based delay
        /// </summary>
        Delay,

        /// <summary>
        /// await UdonTask.DelayFrames(frames) - frame-based delay
        /// </summary>
        DelayFrames,

        /// <summary>
        /// await UdonTask.Yield() - yield to next frame
        /// </summary>
        Yield,

        /// <summary>
        /// await UdonTask.WhenAll(...) - wait for all tasks
        /// </summary>
        WhenAll,

        /// <summary>
        /// await UdonTask.WhenAny(...) - wait for first task
        /// </summary>
        WhenAny,

        /// <summary>
        /// await OtherMethod() - wait for another task-returning method
        /// </summary>
        OtherTask,

        /// <summary>
        /// Unknown await type
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Result of binding an await expression.
    /// </summary>
    public class AwaitBindResult
    {
        /// <summary>
        /// The type of await being performed.
        /// </summary>
        public AwaitType Type { get; set; }

        /// <summary>
        /// The expression for the delay value (for Delay/DelayFrames).
        /// </summary>
        public ExpressionSyntax DelayExpression { get; set; }

        /// <summary>
        /// The task expressions (for WhenAll/WhenAny).
        /// </summary>
        public ExpressionSyntax[] TaskExpressions { get; set; }

        /// <summary>
        /// The expression being awaited (for OtherTask).
        /// </summary>
        public ExpressionSyntax AwaitedExpression { get; set; }

        /// <summary>
        /// The name of the method being called, if applicable.
        /// </summary>
        public string MethodName { get; set; }

        /// <summary>
        /// Whether the await expression returns a value (UdonTask&lt;T&gt;).
        /// </summary>
        public bool ReturnsValue { get; set; }

        /// <summary>
        /// The return type name if ReturnsValue is true.
        /// </summary>
        public string ReturnTypeName { get; set; }

        /// <summary>
        /// Error message if binding failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Whether the binding was successful.
        /// </summary>
        public bool IsValid => Type != AwaitType.Unknown && string.IsNullOrEmpty(ErrorMessage);
    }

    /// <summary>
    /// Binds await expressions to determine their type and extract parameters.
    ///
    /// This is used by the AsyncStateMachineTransformer to generate appropriate
    /// code for each await expression.
    /// </summary>
    public static class AwaitExpressionBinder
    {
        /// <summary>
        /// Analyzes an await expression to determine the await type and parameters.
        /// </summary>
        /// <param name="awaitExpr">The await expression to analyze.</param>
        /// <param name="semanticModel">The semantic model for type resolution.</param>
        /// <returns>The binding result with type and parameters.</returns>
        public static AwaitBindResult BindAwait(
            AwaitExpressionSyntax awaitExpr,
            SemanticModel semanticModel)
        {
            var result = new AwaitBindResult();

            if (awaitExpr == null)
            {
                result.Type = AwaitType.Unknown;
                result.ErrorMessage = "Await expression is null";
                return result;
            }

            var expression = awaitExpr.Expression;
            result.AwaitedExpression = expression;

            // Check if it's a method invocation
            if (expression is InvocationExpressionSyntax invocation)
            {
                return BindInvocation(invocation, semanticModel);
            }

            // Check if it's awaiting a variable or property (another task)
            if (expression is IdentifierNameSyntax ||
                expression is MemberAccessExpressionSyntax)
            {
                result.Type = AwaitType.OtherTask;
                result.MethodName = GetExpressionName(expression);

                // Try to determine if it returns a value
                TryGetReturnType(expression, semanticModel, result);

                return result;
            }

            result.Type = AwaitType.Unknown;
            result.ErrorMessage = $"Unsupported await expression: {expression.Kind()}";
            return result;
        }

        /// <summary>
        /// Binds a method invocation within an await expression.
        /// </summary>
        private static AwaitBindResult BindInvocation(
            InvocationExpressionSyntax invocation,
            SemanticModel semanticModel)
        {
            var result = new AwaitBindResult();
            result.AwaitedExpression = invocation;

            string methodName = GetInvocationMethodName(invocation);
            result.MethodName = methodName;

            // Get the fully qualified method name for better matching
            string fullMethodName = GetFullMethodName(invocation, semanticModel);

            // Check for UdonTask static methods
            if (IsUdonTaskMethod(fullMethodName, methodName))
            {
                return BindUdonTaskMethod(methodName, invocation, semanticModel);
            }

            // It's a call to another method returning UdonTask
            result.Type = AwaitType.OtherTask;
            TryGetReturnType(invocation, semanticModel, result);

            return result;
        }

        /// <summary>
        /// Binds a call to UdonTask.Delay, UdonTask.Yield, etc.
        /// </summary>
        private static AwaitBindResult BindUdonTaskMethod(
            string methodName,
            InvocationExpressionSyntax invocation,
            SemanticModel semanticModel)
        {
            var result = new AwaitBindResult();
            result.MethodName = methodName;
            result.AwaitedExpression = invocation;

            var arguments = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "Delay":
                    result.Type = AwaitType.Delay;
                    if (arguments.Count > 0)
                    {
                        result.DelayExpression = arguments[0].Expression;
                    }
                    else
                    {
                        result.ErrorMessage = "UdonTask.Delay requires a seconds parameter";
                    }
                    break;

                case "DelayFrames":
                    result.Type = AwaitType.DelayFrames;
                    if (arguments.Count > 0)
                    {
                        result.DelayExpression = arguments[0].Expression;
                    }
                    else
                    {
                        result.ErrorMessage = "UdonTask.DelayFrames requires a frames parameter";
                    }
                    break;

                case "Yield":
                    result.Type = AwaitType.Yield;
                    // Yield takes no parameters
                    break;

                case "WhenAll":
                    result.Type = AwaitType.WhenAll;
                    result.TaskExpressions = arguments
                        .Select(a => a.Expression)
                        .ToArray();
                    break;

                case "WhenAny":
                    result.Type = AwaitType.WhenAny;
                    result.TaskExpressions = arguments
                        .Select(a => a.Expression)
                        .ToArray();
                    break;

                default:
                    result.Type = AwaitType.OtherTask;
                    TryGetReturnType(invocation, semanticModel, result);
                    break;
            }

            return result;
        }

        /// <summary>
        /// Gets the method name from an invocation expression.
        /// </summary>
        private static string GetInvocationMethodName(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is IdentifierNameSyntax identifier)
            {
                return identifier.Identifier.Text;
            }

            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                return memberAccess.Name.Identifier.Text;
            }

            if (invocation.Expression is GenericNameSyntax genericName)
            {
                return genericName.Identifier.Text;
            }

            return null;
        }

        /// <summary>
        /// Gets the full method name including type for better matching.
        /// </summary>
        private static string GetFullMethodName(
            InvocationExpressionSyntax invocation,
            SemanticModel semanticModel)
        {
            try
            {
                var symbolInfo = semanticModel?.GetSymbolInfo(invocation);
                if (symbolInfo?.Symbol is IMethodSymbol methodSymbol)
                {
                    return $"{methodSymbol.ContainingType?.ToDisplayString()}.{methodSymbol.Name}";
                }
            }
            catch
            {
                // Ignore errors during symbol resolution
            }

            return null;
        }

        /// <summary>
        /// Checks if the method is a UdonTask static method.
        /// </summary>
        private static bool IsUdonTaskMethod(string fullMethodName, string methodName)
        {
            if (fullMethodName != null)
            {
                return fullMethodName.StartsWith("UdonSharp.CE.Async.UdonTask.") ||
                       fullMethodName.StartsWith("UdonTask.");
            }

            // Fallback: check common method names
            return methodName == "Delay" ||
                   methodName == "DelayFrames" ||
                   methodName == "Yield" ||
                   methodName == "WhenAll" ||
                   methodName == "WhenAny";
        }

        /// <summary>
        /// Gets the expression name for display purposes.
        /// </summary>
        private static string GetExpressionName(ExpressionSyntax expression)
        {
            if (expression is IdentifierNameSyntax identifier)
            {
                return identifier.Identifier.Text;
            }

            if (expression is MemberAccessExpressionSyntax memberAccess)
            {
                return memberAccess.Name.Identifier.Text;
            }

            return expression.ToString();
        }

        /// <summary>
        /// Tries to determine if the awaited expression returns a value.
        /// </summary>
        private static void TryGetReturnType(
            ExpressionSyntax expression,
            SemanticModel semanticModel,
            AwaitBindResult result)
        {
            try
            {
                var typeInfo = semanticModel?.GetTypeInfo(expression);
                if (typeInfo?.Type != null)
                {
                    string typeName = typeInfo.Value.Type.ToDisplayString();

                    // Check if it's UdonTask<T>
                    if (typeName.StartsWith("UdonSharp.CE.Async.UdonTask<"))
                    {
                        result.ReturnsValue = true;

                        // Extract the type parameter
                        int startIndex = typeName.IndexOf('<') + 1;
                        int endIndex = typeName.LastIndexOf('>');
                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            result.ReturnTypeName = typeName.Substring(startIndex, endIndex - startIndex);
                        }
                    }
                    else if (typeName == "UdonSharp.CE.Async.UdonTask")
                    {
                        result.ReturnsValue = false;
                    }
                }
            }
            catch
            {
                // Ignore errors during type resolution
            }
        }

        /// <summary>
        /// Finds all await expressions in a method body.
        /// </summary>
        /// <param name="methodSyntax">The method declaration syntax.</param>
        /// <returns>List of await expressions in order of appearance.</returns>
        public static List<AwaitExpressionSyntax> FindAwaitExpressions(
            MethodDeclarationSyntax methodSyntax)
        {
            if (methodSyntax?.Body == null && methodSyntax?.ExpressionBody == null)
            {
                return new List<AwaitExpressionSyntax>();
            }

            SyntaxNode searchRoot = (SyntaxNode)methodSyntax.Body ?? methodSyntax.ExpressionBody;

            return searchRoot
                .DescendantNodes()
                .OfType<AwaitExpressionSyntax>()
                .ToList();
        }

        /// <summary>
        /// Analyzes all await expressions in a method.
        /// </summary>
        /// <param name="methodSyntax">The method declaration syntax.</param>
        /// <param name="semanticModel">The semantic model for type resolution.</param>
        /// <returns>List of await binding results in order of appearance.</returns>
        public static List<AwaitBindResult> AnalyzeMethod(
            MethodDeclarationSyntax methodSyntax,
            SemanticModel semanticModel)
        {
            var awaitExpressions = FindAwaitExpressions(methodSyntax);
            var results = new List<AwaitBindResult>();

            foreach (var awaitExpr in awaitExpressions)
            {
                var result = BindAwait(awaitExpr, semanticModel);
                results.Add(result);
            }

            return results;
        }
    }
}
