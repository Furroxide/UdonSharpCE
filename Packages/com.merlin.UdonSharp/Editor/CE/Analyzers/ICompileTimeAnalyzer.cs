using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Interface for compile-time analyzers that run after the Bind phase.
    ///
    /// Analyzers detect common issues, anti-patterns, and potential bugs in UdonSharp code.
    /// They integrate with the compiler's diagnostic system to report warnings and errors.
    /// </summary>
    internal interface ICompileTimeAnalyzer
    {
        /// <summary>
        /// Unique identifier for this analyzer (e.g., "CE0001").
        /// Used for filtering and documentation purposes.
        /// </summary>
        string AnalyzerId { get; }

        /// <summary>
        /// Human-readable name for this analyzer.
        /// </summary>
        string AnalyzerName { get; }

        /// <summary>
        /// Description of what this analyzer detects.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Whether this analyzer is enabled by default.
        /// </summary>
        bool IsEnabledByDefault { get; }

        /// <summary>
        /// Runs analysis on a bound type and returns diagnostics.
        ///
        /// This method is called after the Bind phase for each UdonSharpBehaviour type.
        /// The analyzer should examine the type's fields, methods, and bound nodes
        /// to detect issues and return appropriate diagnostics.
        /// </summary>
        /// <param name="type">The bound TypeSymbol to analyze.</param>
        /// <param name="context">The BindContext containing semantic information.</param>
        /// <param name="compilationContext">The CompilationContext for accessing compilation-wide information.</param>
        /// <returns>Collection of diagnostics found during analysis.</returns>
        IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext);
    }
}
