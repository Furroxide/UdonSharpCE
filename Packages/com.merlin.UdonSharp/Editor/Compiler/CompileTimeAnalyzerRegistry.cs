using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

[assembly: InternalsVisibleTo("UdonSharp.CE.Editor")]

namespace UdonSharp.Compiler
{
    /// <summary>
    /// Registry for compile-time analyzers that can be registered by external assemblies.
    /// This allows CE.Editor to register its analyzers without creating a cyclic dependency.
    /// </summary>
    public static class CompileTimeAnalyzerRegistry
    {
        /// <summary>
        /// Delegate signature for analyzer callbacks.
        /// </summary>
        /// <param name="type">The bound TypeSymbol to analyze.</param>
        /// <param name="context">The BindContext for semantic information.</param>
        /// <param name="compilationContext">The CompilationContext for reporting diagnostics.</param>
        internal delegate void AnalyzerCallback(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext);

        private static readonly List<AnalyzerCallback> _registeredAnalyzers = new List<AnalyzerCallback>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Registers an analyzer callback to be invoked during compilation.
        /// </summary>
        /// <param name="analyzer">The analyzer callback to register.</param>
        internal static void RegisterAnalyzer(AnalyzerCallback analyzer)
        {
            if (analyzer == null) return;

            lock (_lock)
            {
                if (!_registeredAnalyzers.Contains(analyzer))
                {
                    _registeredAnalyzers.Add(analyzer);
                }
            }
        }

        /// <summary>
        /// Unregisters an analyzer callback.
        /// </summary>
        /// <param name="analyzer">The analyzer callback to unregister.</param>
        internal static void UnregisterAnalyzer(AnalyzerCallback analyzer)
        {
            if (analyzer == null) return;

            lock (_lock)
            {
                _registeredAnalyzers.Remove(analyzer);
            }
        }

        /// <summary>
        /// Runs all registered analyzers on the given type.
        /// Called by the compiler after the Bind phase.
        /// </summary>
        internal static void RunAnalyzers(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            AnalyzerCallback[] analyzers;

            lock (_lock)
            {
                analyzers = _registeredAnalyzers.ToArray();
            }

            foreach (var analyzer in analyzers)
            {
                try
                {
                    analyzer(type, context, compilationContext);
                }
                catch (Exception ex)
                {
                    ReportCallbackFailure(analyzer, type, compilationContext, ex);
                }
            }
        }

        /// <summary>
        /// Surfaces a failed analyzer callback as a compile diagnostic.
        ///
        /// A throwing callback takes every analyzer behind it in that callback down with it. Reporting that
        /// only to the console made a compile where nothing was checked look identical to a clean one, so
        /// it has to reach the diagnostic list where the rest of the compile's findings are read from.
        /// </summary>
        private static void ReportCallbackFailure(
            AnalyzerCallback analyzer,
            TypeSymbol type,
            CompilationContext compilationContext,
            Exception exception)
        {
            string analyzerName = analyzer.Method != null
                ? $"{analyzer.Method.DeclaringType?.FullName}.{analyzer.Method.Name}"
                : analyzer.ToString();

            string message =
                $"USCE0007: compile-time analyzer callback '{analyzerName}' threw {exception.GetType().Name} while " +
                $"analyzing type '{type?.Name}' and did not finish: {exception.Message}. Analyzers behind it in that " +
                "callback did not run, so this compile's diagnostics are incomplete for this type.";

            if (compilationContext == null)
            {
                UdonSharpCEUtils.LogWarning(message);
                return;
            }

            compilationContext.AddDiagnostic(DiagnosticSeverity.Warning, (Location)null, message);
        }

        /// <summary>
        /// Gets the count of registered analyzers.
        /// </summary>
        public static int AnalyzerCount
        {
            get
            {
                lock (_lock)
                {
                    return _registeredAnalyzers.Count;
                }
            }
        }
    }
}

