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
    /// Phase 3 provides runtime types (UdonTask, CancellationToken) and validates usage.
    /// Full compiler transformation will be added in a future update.
    /// </summary>
    /// <remarks>
    /// Currently checks for:
    /// - Methods returning UdonTask that need async transformation
    /// - Unsupported patterns (nested async lambdas, try-finally in async)
    /// - Informational messages about async state machine generation
    ///
    /// Future updates will implement the actual state machine transformation.
    /// </remarks>
    internal class AsyncMethodAnalyzer : ICompileTimeAnalyzer
    {
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
                // Check if method returns UdonTask or UdonTask<T>
                if (!IsAsyncMethod(method, context))
                    continue;

                var syntaxRefs = method.RoslynSymbol?.DeclaringSyntaxReferences;
                if (!syntaxRefs.HasValue || syntaxRefs.Value.Length == 0)
                    continue;

                var methodSyntax = syntaxRefs.Value.First().GetSyntax() as MethodDeclarationSyntax;
                if (methodSyntax == null)
                    continue;

                Location methodLocation = methodSyntax.GetLocation();

                // Info: This method will be transformed
                diagnostics.Add(AnalyzerDiagnostic.Info(
                    methodLocation,
                    $"Method '{method.Name}' returns UdonTask and will be transformed to a state machine. " +
                    "Note: Full async transformation is in development.",
                    AnalyzerId,
                    AnalyzerId
                ));

                // Check for unsupported patterns
                CheckForUnsupportedPatterns(methodSyntax, method, diagnostics);
            }

            return diagnostics;
        }

        /// <summary>
        /// Checks if a method returns UdonTask or UdonTask&lt;T&gt;.
        /// </summary>
        private bool IsAsyncMethod(MethodSymbol method, BindContext context)
        {
            if (method.ReturnType == null)
                return false;

            string returnTypeName = method.ReturnType.RoslynSymbol?.ToString() ?? "";

            return returnTypeName == "UdonSharp.CE.Async.UdonTask" ||
                   returnTypeName.StartsWith("UdonSharp.CE.Async.UdonTask<");
        }

        /// <summary>
        /// Checks for patterns that are not supported in async methods.
        /// </summary>
        private void CheckForUnsupportedPatterns(
            MethodDeclarationSyntax methodSyntax,
            MethodSymbol method,
            List<AnalyzerDiagnostic> diagnostics)
        {
            // Check for async modifier (shouldn't be used with UdonTask)
            if (methodSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                diagnostics.Add(AnalyzerDiagnostic.Warning(
                    methodSyntax.GetLocation(),
                    $"Method '{method.Name}' has 'async' modifier but returns UdonTask. " +
                    "Remove the 'async' modifier - UdonTask methods are transformed differently from C# async.",
                    AnalyzerId,
                    AnalyzerId,
                    "Remove the 'async' keyword from the method signature."
                ));
            }

            // Check for await expressions (currently informational)
            var awaitExpressions = methodSyntax.DescendantNodes()
                .OfType<AwaitExpressionSyntax>();

            int awaitCount = awaitExpressions.Count();
            if (awaitCount > 0)
            {
                diagnostics.Add(AnalyzerDiagnostic.Info(
                    methodSyntax.GetLocation(),
                    $"Method '{method.Name}' contains {awaitCount} await expression(s). " +
                    "These will be transformed to state machine yield points.",
                    AnalyzerId,
                    AnalyzerId
                ));
            }

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

            // Check for try-finally around await (complex state machine)
            var tryStatements = methodSyntax.DescendantNodes()
                .OfType<TryStatementSyntax>()
                .Where(t => t.Finally != null);

            foreach (var tryStmt in tryStatements)
            {
                // Check if there's an await inside the try block
                bool hasAwaitInTry = tryStmt.Block.DescendantNodes()
                    .OfType<AwaitExpressionSyntax>().Any();

                if (hasAwaitInTry)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Warning(
                        tryStmt.GetLocation(),
                        "try-finally with await inside the try block may not behave as expected. " +
                        "The finally block runs immediately when exiting the try, not after await completes.",
                        AnalyzerId,
                        AnalyzerId,
                        "Move the await outside the try block or restructure the code."
                    ));
                }
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
