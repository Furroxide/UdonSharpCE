using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using UnityEditor;
using UnityEngine;
using DiagnosticSeverity = UdonSharp.Compiler.DiagnosticSeverity;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Orchestrates running all CE analyzers after the Bind phase.
    ///
    /// This class manages the collection of registered analyzers and executes them
    /// in a thread-safe manner during compilation.
    /// </summary>
    [InitializeOnLoad]
    internal static class CEAnalyzerRunner
    {
        /// <summary>
        /// Static constructor registers with the compiler's analyzer registry.
        /// </summary>
        static CEAnalyzerRunner()
        {
            CompileTimeAnalyzerRegistry.RegisterAnalyzer(RunAnalyzers);
        }

        /// <summary>
        /// The collection of all registered analyzers.
        /// </summary>
        private static readonly ICompileTimeAnalyzer[] Analyzers = new ICompileTimeAnalyzer[]
        {
            // Phase 2: Core analyzers
            new UninitializedSyncArrayAnalyzer(),
            new GetComponentInUpdateAnalyzer(),
            new SyncPayloadSizeAnalyzer(),

            // Phase 3: CE.Net analyzers
            new SyncFieldAnalyzer(),
            new DeltaEncodeAdvisoryAnalyzer(),
            new SyncOnJoinSupportAnalyzer(),
            new RpcMethodAnalyzer(),
            new LocalOnlyAnalyzer(),

            // CE.Net silent-failure analyzers. Every one of these describes a bug that produces no error
            // at runtime -- a discarded write, a prediction nothing ever corrects, an unserialized Manual
            // behaviour, an empty payload, a saturated network, a network event that never leaves the
            // client.
            new SyncedFieldOwnershipAnalyzer(),
            new PredictedFieldReconciliationAnalyzer(),
            new ManualSyncWithoutSerializationAnalyzer(),
            new ManualSyncWithoutSyncedFieldsAnalyzer(),
            new RequestSerializationRateAnalyzer(),
            new NetworkEventSyncModeAnalyzer(),

            // Phase 3: CE.Async analyzers
            new AsyncMethodAnalyzer(),

            // Phase 2: Lambda validation
            new LambdaUsageAnalyzer(),

            // Phase 2: CE.Persistence analyzers
            new PlayerDataSizeAnalyzer()
        };

        /// <summary>
        /// Diagnostic code used when an analyzer itself fails. It gets a real CE code so a crashing
        /// analyzer is searchable and filterable like any other finding.
        /// </summary>
        internal const string AnalyzerFailureCode = "CE0019";

        /// <summary>
        /// Whether to log analyzer execution for debugging.
        /// </summary>
        private static bool _enableDebugLogging = false;

        /// <summary>
        /// Runs all enabled analyzers on a bound type and reports diagnostics.
        ///
        /// This method is thread-safe and can be called from parallel compilation.
        /// Each analyzer is run in isolation and failures in one analyzer do not
        /// affect other analyzers, but a failure is reported as a compile diagnostic
        /// rather than only a console warning -- see <see cref="ReportAnalyzerFailure"/>.
        /// </summary>
        /// <param name="type">The bound TypeSymbol to analyze.</param>
        /// <param name="context">The BindContext for semantic information.</param>
        /// <param name="compilationContext">The CompilationContext for reporting diagnostics.</param>
        public static void RunAnalyzers(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            if (type == null || context == null || compilationContext == null)
                return;

            // Only analyze UdonSharpBehaviour types
            if (!type.IsUdonSharpBehaviour)
                return;

            if (_enableDebugLogging)
            {
                Debug.Log($"[CE.Analyzers] Running analyzers on {type.Name}");
            }

            foreach (var analyzer in Analyzers)
            {
                if (!analyzer.IsEnabledByDefault)
                    continue;

                try
                {
                    RunSingleAnalyzer(analyzer, type, context, compilationContext);
                }
                catch (Exception ex)
                {
                    ReportAnalyzerFailure(analyzer, type, compilationContext, ex);
                }
            }
        }

        /// <summary>
        /// Surfaces an analyzer crash as a compile diagnostic.
        ///
        /// This used to be a bare <c>Debug.LogWarning</c>, which meant a crashing analyzer stopped checking
        /// and the compile still looked clean: an analyzer that never ran is indistinguishable from an
        /// analyzer that found nothing. The severity stays at Warning because an analyzer only reports on
        /// code, it does not generate any -- a bug in one must not brick a build the way a failed generator
        /// deliberately does.
        /// </summary>
        private static void ReportAnalyzerFailure(
            ICompileTimeAnalyzer analyzer,
            TypeSymbol type,
            CompilationContext compilationContext,
            Exception exception)
        {
            string message =
                $"[{AnalyzerFailureCode}] Analyzer {analyzer.AnalyzerId} ({analyzer.AnalyzerName}) threw " +
                $"{exception.GetType().Name} while analyzing '{type.Name}' and did not finish: {exception.Message}. " +
                $"The checks owned by {analyzer.AnalyzerId} did not run for this behaviour, so its absence from this " +
                "compile's diagnostics does not mean the behaviour is clean.";

            compilationContext.AddDiagnostic(DiagnosticSeverity.Warning, GetTypeLocation(type), message);

            if (_enableDebugLogging)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>
        /// Gets the Roslyn Location for a type symbol so an analyzer failure points at the behaviour it
        /// gave up on rather than at the whole compile.
        /// </summary>
        private static Location GetTypeLocation(TypeSymbol type)
        {
            var declaringSyntaxRefs = type?.RoslynSymbol?.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Value.Length > 0)
            {
                return declaringSyntaxRefs.Value[0].GetSyntax().GetLocation();
            }

            var locations = type?.RoslynSymbol?.Locations;
            if (locations != null && locations.Value.Length > 0)
            {
                return locations.Value[0];
            }

            return Location.None;
        }

        /// <summary>
        /// Runs a single analyzer and reports its diagnostics.
        /// </summary>
        private static void RunSingleAnalyzer(
            ICompileTimeAnalyzer analyzer,
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = analyzer.Analyze(type, context, compilationContext);

            if (diagnostics == null)
                return;

            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic == null)
                    continue;

                // Report through the compiler's diagnostic system
                compilationContext.AddDiagnostic(
                    diagnostic.Severity,
                    diagnostic.Location,
                    diagnostic.GetFormattedMessage());

                if (_enableDebugLogging)
                {
                    Debug.Log($"[CE.Analyzers] {diagnostic.Code}: {diagnostic.Message}");
                }
            }
        }

        /// <summary>
        /// Gets information about all registered analyzers.
        /// </summary>
        /// <returns>Array of analyzer information tuples.</returns>
        public static (string Id, string Name, string Description, bool Enabled)[] GetAnalyzerInfo()
        {
            var info = new (string, string, string, bool)[Analyzers.Length];

            for (int i = 0; i < Analyzers.Length; i++)
            {
                var analyzer = Analyzers[i];
                info[i] = (analyzer.AnalyzerId, analyzer.AnalyzerName, analyzer.Description, analyzer.IsEnabledByDefault);
            }

            return info;
        }

        /// <summary>
        /// Enables or disables debug logging for analyzer execution.
        /// </summary>
        public static void SetDebugLogging(bool enabled)
        {
            _enableDebugLogging = enabled;
        }
    }
}
