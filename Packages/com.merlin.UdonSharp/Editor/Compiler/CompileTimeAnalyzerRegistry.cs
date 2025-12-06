using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
                    UnityEngine.Debug.LogWarning($"[UdonSharp] Analyzer failed on type {type?.Name}: {ex.Message}");
                }
            }
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

