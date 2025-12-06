using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Async
{
    /// <summary>
    /// Context for transforming an async method to a state machine.
    /// </summary>
    public class AsyncMethodContext
    {
        /// <summary>
        /// The original method name.
        /// </summary>
        public string MethodName { get; set; }

        /// <summary>
        /// The name of the generated state field.
        /// </summary>
        public string StateFieldName { get; set; }

        /// <summary>
        /// The name of the generated result field.
        /// </summary>
        public string ResultFieldName { get; set; }

        /// <summary>
        /// The name of the generated MoveNext method.
        /// </summary>
        public string MoveNextMethodName { get; set; }

        /// <summary>
        /// Variables that need to be hoisted to instance fields.
        /// </summary>
        public List<HoistedVariable> HoistedVariables { get; set; } = new List<HoistedVariable>();

        /// <summary>
        /// Await points in the method.
        /// </summary>
        public List<AwaitPoint> AwaitPoints { get; set; } = new List<AwaitPoint>();

        /// <summary>
        /// The method's return type (UdonTask or UdonTask&lt;T&gt;).
        /// </summary>
        public string ReturnType { get; set; }

        /// <summary>
        /// Whether the method returns a value (UdonTask&lt;T&gt;).
        /// </summary>
        public bool ReturnsValue { get; set; }

        /// <summary>
        /// The return value type if ReturnsValue is true.
        /// </summary>
        public string ReturnValueType { get; set; }

        /// <summary>
        /// Whether the method accepts a CancellationToken parameter.
        /// </summary>
        public bool HasCancellationToken { get; set; }

        /// <summary>
        /// The cancellation token parameter name.
        /// </summary>
        public string CancellationTokenName { get; set; }
    }

    /// <summary>
    /// Represents a local variable that needs to be hoisted to an instance field.
    /// </summary>
    public class HoistedVariable
    {
        /// <summary>
        /// The original variable name.
        /// </summary>
        public string OriginalName { get; set; }

        /// <summary>
        /// The generated field name.
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>
        /// The variable's type.
        /// </summary>
        public string TypeName { get; set; }
    }

    /// <summary>
    /// Represents an await point in the method.
    /// </summary>
    public class AwaitPoint
    {
        /// <summary>
        /// The state ID for this await point.
        /// </summary>
        public int StateId { get; set; }

        /// <summary>
        /// The await expression syntax.
        /// </summary>
        public AwaitExpressionSyntax AwaitExpression { get; set; }

        /// <summary>
        /// The binding result for this await.
        /// </summary>
        public AwaitBindResult BindResult { get; set; }

        /// <summary>
        /// The code before this await point (from previous await or method start).
        /// </summary>
        public string CodeBefore { get; set; }
    }

    /// <summary>
    /// Result of state machine transformation.
    /// </summary>
    public class TransformResult
    {
        /// <summary>
        /// Whether the transformation was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Error message if transformation failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Generated field declarations.
        /// </summary>
        public string GeneratedFields { get; set; }

        /// <summary>
        /// The transformed entry method code.
        /// </summary>
        public string TransformedMethod { get; set; }

        /// <summary>
        /// The generated MoveNext method code.
        /// </summary>
        public string MoveNextMethod { get; set; }

        /// <summary>
        /// The async method context.
        /// </summary>
        public AsyncMethodContext Context { get; set; }
    }

    /// <summary>
    /// Transforms async methods returning UdonTask into state machine implementations.
    ///
    /// The transformer converts:
    /// <code>
    /// public UdonTask PlayCutscene()
    /// {
    ///     await UdonTask.Delay(1f);
    ///     DoSomething();
    ///     await UdonTask.Delay(2f);
    /// }
    /// </code>
    ///
    /// Into:
    /// <code>
    /// private int __PlayCutscene_state;
    /// private UdonTask __PlayCutscene_result;
    ///
    /// public UdonTask PlayCutscene()
    /// {
    ///     __PlayCutscene_state = 0;
    ///     __PlayCutscene_MoveNext();
    ///     return __PlayCutscene_result;
    /// }
    ///
    /// public void __PlayCutscene_MoveNext()
    /// {
    ///     switch (__PlayCutscene_state)
    ///     {
    ///         case 0:
    ///             __PlayCutscene_state = 1;
    ///             SendCustomEventDelayedSeconds(nameof(__PlayCutscene_MoveNext), 1f);
    ///             return;
    ///         case 1:
    ///             DoSomething();
    ///             __PlayCutscene_state = 2;
    ///             SendCustomEventDelayedSeconds(nameof(__PlayCutscene_MoveNext), 2f);
    ///             return;
    ///         case 2:
    ///             __PlayCutscene_result.SetCompleted();
    ///             return;
    ///     }
    /// }
    /// </code>
    /// </summary>
    public static class AsyncStateMachineTransformer
    {
        private const string STATE_PREFIX = "__";
        private const string STATE_SUFFIX = "_state";
        private const string RESULT_SUFFIX = "_result";
        private const string MOVENEXT_SUFFIX = "_MoveNext";
        private const string HOISTED_PREFIX = "__";

        /// <summary>
        /// Transforms an async method to a state machine.
        /// </summary>
        /// <param name="methodSyntax">The method syntax to transform.</param>
        /// <param name="semanticModel">The semantic model for type resolution.</param>
        /// <returns>The transformation result.</returns>
        public static TransformResult Transform(
            MethodDeclarationSyntax methodSyntax,
            SemanticModel semanticModel)
        {
            var result = new TransformResult();

            try
            {
                // Build context
                var context = BuildContext(methodSyntax, semanticModel);
                result.Context = context;

                // Find all await expressions
                var awaitExpressions = AwaitExpressionBinder.FindAwaitExpressions(methodSyntax);

                if (awaitExpressions.Count == 0)
                {
                    // No awaits - just return a completed task
                    result.Success = true;
                    result.TransformedMethod = GenerateNoAwaitMethod(context, methodSyntax);
                    return result;
                }

                // Bind await expressions
                var bindResults = AwaitExpressionBinder.AnalyzeMethod(methodSyntax, semanticModel);

                // Create await points
                context.AwaitPoints = CreateAwaitPoints(methodSyntax, awaitExpressions, bindResults);

                // Find hoisted variables
                context.HoistedVariables = FindHoistedVariables(methodSyntax, context.AwaitPoints);

                // Generate code
                result.GeneratedFields = GenerateFields(context);
                result.TransformedMethod = GenerateEntryMethod(context, methodSyntax);
                result.MoveNextMethod = GenerateMoveNextMethod(context, methodSyntax);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Builds the transformation context from method syntax.
        /// </summary>
        private static AsyncMethodContext BuildContext(
            MethodDeclarationSyntax methodSyntax,
            SemanticModel semanticModel)
        {
            var context = new AsyncMethodContext();

            string methodName = methodSyntax.Identifier.Text;
            context.MethodName = methodName;
            context.StateFieldName = $"{STATE_PREFIX}{methodName}{STATE_SUFFIX}";
            context.ResultFieldName = $"{STATE_PREFIX}{methodName}{RESULT_SUFFIX}";
            context.MoveNextMethodName = $"{STATE_PREFIX}{methodName}{MOVENEXT_SUFFIX}";

            // Parse return type
            string returnType = methodSyntax.ReturnType.ToString();
            context.ReturnType = returnType;

            if (returnType.Contains("<"))
            {
                context.ReturnsValue = true;
                int start = returnType.IndexOf('<') + 1;
                int end = returnType.LastIndexOf('>');
                if (start > 0 && end > start)
                {
                    context.ReturnValueType = returnType.Substring(start, end - start);
                }
            }

            // Check for CancellationToken parameter
            foreach (var param in methodSyntax.ParameterList.Parameters)
            {
                string paramType = param.Type?.ToString() ?? "";
                if (paramType.Contains("CancellationToken"))
                {
                    context.HasCancellationToken = true;
                    context.CancellationTokenName = param.Identifier.Text;
                    break;
                }
            }

            return context;
        }

        /// <summary>
        /// Creates await points from the list of await expressions.
        /// </summary>
        private static List<AwaitPoint> CreateAwaitPoints(
            MethodDeclarationSyntax methodSyntax,
            List<AwaitExpressionSyntax> awaitExpressions,
            List<AwaitBindResult> bindResults)
        {
            var awaitPoints = new List<AwaitPoint>();

            for (int i = 0; i < awaitExpressions.Count; i++)
            {
                var awaitPoint = new AwaitPoint
                {
                    StateId = i,
                    AwaitExpression = awaitExpressions[i],
                    BindResult = i < bindResults.Count ? bindResults[i] : new AwaitBindResult { Type = AwaitType.Unknown }
                };

                awaitPoints.Add(awaitPoint);
            }

            return awaitPoints;
        }

        /// <summary>
        /// Finds local variables that need to be hoisted.
        /// </summary>
        private static List<HoistedVariable> FindHoistedVariables(
            MethodDeclarationSyntax methodSyntax,
            List<AwaitPoint> awaitPoints)
        {
            var hoistedVariables = new List<HoistedVariable>();

            if (methodSyntax.Body == null)
                return hoistedVariables;

            // Find all local variable declarations
            var localDeclarations = methodSyntax.Body
                .DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>();

            // Find all variable usages across await points
            var awaitSpans = awaitPoints.Select(ap => ap.AwaitExpression.SpanStart).ToList();

            foreach (var localDecl in localDeclarations)
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    string varName = variable.Identifier.Text;
                    int declPosition = variable.SpanStart;

                    // Check if this variable is used after any await point that comes after its declaration
                    bool crossesAwait = false;

                    foreach (var awaitSpan in awaitSpans)
                    {
                        if (awaitSpan > declPosition)
                        {
                            // Check if the variable is used after this await
                            var usagesAfterAwait = methodSyntax.Body
                                .DescendantNodes()
                                .OfType<IdentifierNameSyntax>()
                                .Where(id => id.Identifier.Text == varName && id.SpanStart > awaitSpan);

                            if (usagesAfterAwait.Any())
                            {
                                crossesAwait = true;
                                break;
                            }
                        }
                    }

                    if (crossesAwait)
                    {
                        hoistedVariables.Add(new HoistedVariable
                        {
                            OriginalName = varName,
                            FieldName = $"{HOISTED_PREFIX}{methodSyntax.Identifier.Text}_{varName}",
                            TypeName = localDecl.Declaration.Type.ToString()
                        });
                    }
                }
            }

            return hoistedVariables;
        }

        /// <summary>
        /// Generates the field declarations for the state machine.
        /// </summary>
        private static string GenerateFields(AsyncMethodContext context)
        {
            var sb = new StringBuilder();

            // State field
            sb.AppendLine($"private int {context.StateFieldName};");

            // Result field
            sb.AppendLine($"private {context.ReturnType} {context.ResultFieldName};");

            // Hoisted variable fields
            foreach (var hoisted in context.HoistedVariables)
            {
                sb.AppendLine($"private {hoisted.TypeName} {hoisted.FieldName};");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generates the entry method that initializes and starts the state machine.
        /// </summary>
        private static string GenerateEntryMethod(
            AsyncMethodContext context,
            MethodDeclarationSyntax originalMethod)
        {
            var sb = new StringBuilder();

            // Method signature
            string modifiers = string.Join(" ", originalMethod.Modifiers.Select(m => m.Text));
            string parameters = originalMethod.ParameterList.ToString();

            sb.AppendLine($"{modifiers} {context.ReturnType} {context.MethodName}{parameters}");
            sb.AppendLine("{");

            // Initialize state
            sb.AppendLine($"    {context.StateFieldName} = 0;");

            // Initialize result
            if (context.ReturnsValue)
            {
                sb.AppendLine($"    {context.ResultFieldName} = new {context.ReturnType}();");
            }
            else
            {
                sb.AppendLine($"    {context.ResultFieldName} = new UdonTask();");
            }

            // Call MoveNext
            sb.AppendLine($"    {context.MoveNextMethodName}();");

            // Return result
            sb.AppendLine($"    return {context.ResultFieldName};");

            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates the MoveNext method with state machine switch.
        /// </summary>
        private static string GenerateMoveNextMethod(
            AsyncMethodContext context,
            MethodDeclarationSyntax originalMethod)
        {
            var sb = new StringBuilder();

            // Method signature
            sb.AppendLine($"public void {context.MoveNextMethodName}()");
            sb.AppendLine("{");

            // Cancellation check
            if (context.HasCancellationToken)
            {
                sb.AppendLine($"    if ({context.CancellationTokenName}.IsCancellationRequested)");
                sb.AppendLine("    {");
                sb.AppendLine($"        {context.ResultFieldName}.SetCanceled();");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            // Switch statement
            sb.AppendLine($"    switch ({context.StateFieldName})");
            sb.AppendLine("    {");

            // Generate case for each state
            for (int i = 0; i <= context.AwaitPoints.Count; i++)
            {
                sb.AppendLine($"        case {i}:");

                if (i < context.AwaitPoints.Count)
                {
                    // Generate code for this state
                    GenerateStateCase(sb, context, i);
                }
                else
                {
                    // Final state - complete the task
                    GenerateFinalState(sb, context);
                }

                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates code for a single state case.
        /// </summary>
        private static void GenerateStateCase(
            StringBuilder sb,
            AsyncMethodContext context,
            int stateIndex)
        {
            var awaitPoint = context.AwaitPoints[stateIndex];
            var bindResult = awaitPoint.BindResult;

            // Set next state
            int nextState = stateIndex + 1;
            sb.AppendLine($"            {context.StateFieldName} = {nextState};");

            // Generate await handling based on type
            switch (bindResult.Type)
            {
                case AwaitType.Delay:
                    string delayExpr = bindResult.DelayExpression?.ToString() ?? "0f";
                    sb.AppendLine($"            SendCustomEventDelayedSeconds(nameof({context.MoveNextMethodName}), {delayExpr});");
                    break;

                case AwaitType.DelayFrames:
                    string framesExpr = bindResult.DelayExpression?.ToString() ?? "1";
                    sb.AppendLine($"            SendCustomEventDelayedFrames(nameof({context.MoveNextMethodName}), {framesExpr});");
                    break;

                case AwaitType.Yield:
                    sb.AppendLine($"            SendCustomEventDelayedFrames(nameof({context.MoveNextMethodName}), 1);");
                    break;

                case AwaitType.WhenAll:
                case AwaitType.WhenAny:
                    // For WhenAll/WhenAny, we need to poll
                    sb.AppendLine("            // TODO: WhenAll/WhenAny polling not yet implemented");
                    sb.AppendLine($"            SendCustomEventDelayedFrames(nameof({context.MoveNextMethodName}), 1);");
                    break;

                case AwaitType.OtherTask:
                    // For other tasks, we need to poll completion
                    sb.AppendLine("            // TODO: Task polling not yet implemented");
                    sb.AppendLine($"            SendCustomEventDelayedFrames(nameof({context.MoveNextMethodName}), 1);");
                    break;

                default:
                    sb.AppendLine("            // Unknown await type");
                    sb.AppendLine($"            SendCustomEventDelayedFrames(nameof({context.MoveNextMethodName}), 1);");
                    break;
            }

            sb.AppendLine("            return;");
        }

        /// <summary>
        /// Generates the final state that completes the task.
        /// </summary>
        private static void GenerateFinalState(
            StringBuilder sb,
            AsyncMethodContext context)
        {
            if (context.ReturnsValue)
            {
                sb.AppendLine($"            // TODO: Set result value");
                sb.AppendLine($"            {context.ResultFieldName}.SetCompleted();");
            }
            else
            {
                sb.AppendLine($"            {context.ResultFieldName}.SetCompleted();");
            }
            sb.AppendLine("            return;");
        }

        /// <summary>
        /// Generates a method with no await expressions.
        /// </summary>
        private static string GenerateNoAwaitMethod(
            AsyncMethodContext context,
            MethodDeclarationSyntax originalMethod)
        {
            var sb = new StringBuilder();

            string modifiers = string.Join(" ", originalMethod.Modifiers.Select(m => m.Text));
            string parameters = originalMethod.ParameterList.ToString();

            sb.AppendLine($"{modifiers} {context.ReturnType} {context.MethodName}{parameters}");
            sb.AppendLine("{");

            // Execute body
            if (originalMethod.Body != null)
            {
                // Insert original body statements (minus the braces)
                string bodyCode = originalMethod.Body.ToString();
                bodyCode = bodyCode.TrimStart('{').TrimEnd('}').Trim();

                foreach (var line in bodyCode.Split('\n'))
                {
                    sb.AppendLine($"    {line.TrimEnd()}");
                }
            }

            // Return completed task
            if (context.ReturnsValue)
            {
                sb.AppendLine($"    return UdonTask<{context.ReturnValueType}>.CompletedTask;");
            }
            else
            {
                sb.AppendLine("    return UdonTask.CompletedTask;");
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Validates whether a method can be transformed.
        /// </summary>
        /// <param name="methodSyntax">The method to validate.</param>
        /// <returns>Validation result with success flag and error message.</returns>
        public static (bool IsValid, string ErrorMessage) ValidateMethod(
            MethodDeclarationSyntax methodSyntax)
        {
            // Check for expression body (not supported)
            if (methodSyntax.ExpressionBody != null)
            {
                return (false, "Expression-bodied async methods are not supported. Use a block body instead.");
            }

            // Check for nested functions/lambdas with await
            var nestedAwaits = methodSyntax.Body?
                .DescendantNodes()
                .Where(n => n is LocalFunctionStatementSyntax ||
                            n is ParenthesizedLambdaExpressionSyntax ||
                            n is SimpleLambdaExpressionSyntax)
                .SelectMany(n => n.DescendantNodes().OfType<AwaitExpressionSyntax>());

            if (nestedAwaits != null && nestedAwaits.Any())
            {
                return (false, "Await expressions in nested functions or lambdas are not supported.");
            }

            // Check for goto statements
            var gotos = methodSyntax.Body?.DescendantNodes().OfType<GotoStatementSyntax>();
            if (gotos != null && gotos.Any())
            {
                return (false, "Goto statements are not supported in async methods.");
            }

            return (true, null);
        }
    }
}
