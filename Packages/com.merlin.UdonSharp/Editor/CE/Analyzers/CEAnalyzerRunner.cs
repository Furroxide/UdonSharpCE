using System;
using System.Collections.Generic;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using UnityEditor;
using UnityEngine;

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
            new RpcMethodAnalyzer(),
            new LocalOnlyAnalyzer(),

            // Phase 3: CE.Async analyzers
            new AsyncMethodAnalyzer(),

            // Phase 2: CE.Persistence analyzers
            new PlayerDataSizeAnalyzer()
        };

        /// <summary>
        /// Whether to log analyzer execution for debugging.
        /// </summary>
        private static bool _enableDebugLogging = false;

        /// <summary>
        /// Runs all enabled analyzers on a bound type and reports diagnostics.
        ///
        /// This method is thread-safe and can be called from parallel compilation.
        /// Each analyzer is run in isolation and failures in one analyzer do not
        /// affect other analyzers.
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
                    // Log but don't fail compilation for analyzer errors
                    Debug.LogWarning($"[CE.Analyzers] Analyzer {analyzer.AnalyzerId} ({analyzer.AnalyzerName}) failed on type {type.Name}: {ex.Message}");

                    if (_enableDebugLogging)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
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
