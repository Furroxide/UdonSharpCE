using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Async;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Validates async method patterns and warns about unsupported scenarios.
    ///
    /// CE lowers supported UdonTask async methods into event-driven state machines.
    /// </summary>
    /// <remarks>
    /// The codes reported here are the same ones the async transform aborts the compile with, so an author
    /// who runs analysis first sees the problem before the generator turns it into a build failure.
    /// Currently checks for:
    /// - async UdonTask&lt;T&gt;, which has no point at which to hand a result back (CE0022)
    /// - async UdonTask with no await, which the C# compiler would turn into a real state machine (CE0023)
    /// - Unsupported patterns (nested async lambdas, await inside try, yield return)
    /// - Informational messages about async state machine generation
    /// </remarks>
    internal class AsyncMethodAnalyzer : ICompileTimeAnalyzer
    {
        private const string UdonTaskOfTCode = "CE0022";
        private const string AsyncWithoutAwaitCode = "CE0023";
        private const string UnsupportedPatternCode = "CE0025";

        public string AnalyzerId => "CE0020";
        public string AnalyzerName => "Async Method Validation";
        public string Description => "Validates async/await patterns and warns about unsupported scenarios.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            var methods = type.GetMembers<MethodSymbol>(context);

            foreach (MethodSymbol method in methods)
            {
                if (!ReturnsUdonTask(method, out bool isGeneric))
                    continue;

                var syntaxRefs = method.RoslynSymbol?.DeclaringSyntaxReferences;
                if (!syntaxRefs.HasValue || syntaxRefs.Value.Length == 0)
                    continue;

                var methodSyntax = syntaxRefs.Value.First().GetSyntax() as MethodDeclarationSyntax;
                if (methodSyntax == null)
                    continue;

                // A UdonTask-returning method that is not declared async is ordinary code -- typically a
                // helper that returns UdonTask.CompletedTask. Announcing a state machine for it described a
                // transform that never happens.
                if (!methodSyntax.Modifiers.Any(SyntaxKind.AsyncKeyword))
                    continue;

                if (isGeneric)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        methodSyntax.ReturnType.GetLocation(),
                        $"'{method.Name}' is declared 'async UdonTask<T>', which CE cannot lower. The state " +
                        "machine returns to its caller at the first await, so there is no point at which a " +
                        "result can be handed back.",
                        AnalyzerId,
                        UdonTaskOfTCode,
                        "Return plain 'UdonTask' and store the result in a field the caller reads once the " +
                        "continuation runs."
                    ));
                    continue;
                }

                int awaitCount = methodSyntax.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();
                if (awaitCount == 0)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        methodSyntax.Identifier.GetLocation(),
                        $"'{method.Name}' is declared async but contains no 'await', so CE has nothing to " +
                        "lower and the C# compiler would build a state machine Udon cannot run.",
                        AnalyzerId,
                        AsyncWithoutAwaitCode,
                        "Drop the 'async' keyword and 'return UdonTask.CompletedTask;'."
                    ));
                    continue;
                }

                diagnostics.Add(AnalyzerDiagnostic.Info(
                    methodSyntax.GetLocation(),
                    $"Method '{method.Name}' contains {awaitCount} await expression(s) and will be lowered " +
                    "to a CE state machine.",
                    AnalyzerId,
                    AnalyzerId
                ));

                // Check for unsupported patterns
                CheckForUnsupportedPatterns(methodSyntax, diagnostics);
            }

            return diagnostics;
        }

        /// <summary>
        /// Checks if a method returns UdonTask or UdonTask&lt;T&gt;.
        /// </summary>
        private static bool ReturnsUdonTask(MethodSymbol method, out bool isGeneric)
        {
            isGeneric = false;

            if (method.ReturnType == null)
                return false;

            string returnTypeName = method.ReturnType.RoslynSymbol?.ToString() ?? "";

            if (string.Equals(returnTypeName, "UdonSharp.CE.Async.UdonTask", StringComparison.Ordinal))
                return true;

            if (returnTypeName.StartsWith("UdonSharp.CE.Async.UdonTask<", StringComparison.Ordinal))
            {
                isGeneric = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks for patterns that are not supported in async methods.
        /// </summary>
        private void CheckForUnsupportedPatterns(
            MethodDeclarationSyntax methodSyntax,
            List<AnalyzerDiagnostic> diagnostics)
        {
            // Check for nested async lambdas (not supported)
            var asyncLambdas = methodSyntax.DescendantNodes()
                .Where(n => n is ParenthesizedLambdaExpressionSyntax ||
                           n is SimpleLambdaExpressionSyntax)
                .Where(n =>
                {
                    if (n is ParenthesizedLambdaExpressionSyntax pLambda)
                        return pLambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
                    if (n is SimpleLambdaExpressionSyntax sLambda)
                        return sLambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
                    return false;
                });

            foreach (var lambda in asyncLambdas)
            {
                diagnostics.Add(AnalyzerDiagnostic.Error(
                    lambda.GetLocation(),
                    "Nested async lambdas are not supported in UdonTask methods.",
                    AnalyzerId,
                    AnalyzerId,
                    "Extract the async lambda to a separate method."
                ));
            }

            // Check for await inside try. The transform splits the method at each await, which would tear
            // the try apart across state machine cases, so it refuses outright rather than emitting a try
            // whose handler no longer covers the code that follows the await.
            var tryStatements = methodSyntax.DescendantNodes()
                .OfType<TryStatementSyntax>()
                .Where(tryStmt => tryStmt.DescendantNodes().OfType<AwaitExpressionSyntax>().Any());

            foreach (var tryStmt in tryStatements)
            {
                diagnostics.Add(AnalyzerDiagnostic.Error(
                    tryStmt.GetLocation(),
                    "An await inside a try block is not supported in UdonTask methods.",
                    AnalyzerId,
                    UnsupportedPatternCode,
                    "Move the await outside the try block. Udon has no exception handling at runtime either."
                ));
            }

            // Check for yield return (not supported with async)
            var yieldStatements = methodSyntax.DescendantNodes()
                .OfType<YieldStatementSyntax>();

            if (yieldStatements.Any())
            {
                diagnostics.Add(AnalyzerDiagnostic.Error(
                    yieldStatements.First().GetLocation(),
                    "yield return/break is not supported in UdonTask methods.",
                    AnalyzerId,
                    AnalyzerId,
                    "Use UdonTask.Yield() or UdonTask.Delay() instead."
                ));
            }
        }
    }
}
