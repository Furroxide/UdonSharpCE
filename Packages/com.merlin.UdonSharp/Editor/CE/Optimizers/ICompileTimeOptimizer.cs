using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Interface for compile-time optimizers that transform syntax trees before Roslyn compilation.
    ///
    /// Optimizers transform C# syntax trees to generate more efficient Udon code.
    /// They run during the Setup phase, after syntax trees are loaded but before
    /// Roslyn compilation begins.
    /// </summary>
    internal interface ICompileTimeOptimizer
    {
        /// <summary>
        /// Unique identifier for this optimizer (e.g., "CEOPT003").
        /// Used for filtering, documentation, and reports.
        /// </summary>
        string OptimizerId { get; }

        /// <summary>
        /// Human-readable name for this optimizer.
        /// </summary>
        string OptimizerName { get; }

        /// <summary>
        /// Description of what this optimizer does.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Whether this optimizer is enabled by default.
        /// Most automatic optimizations should be enabled by default.
        /// </summary>
        bool IsEnabledByDefault { get; }

        /// <summary>
        /// Priority for execution order. Lower values run first.
        /// Use 0-99 for early transformations (async/await, callbacks),
        /// 100-199 for standard optimizations (loop unrolling, CSE),
        /// 200+ for late optimizations (string interning).
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Transforms a syntax tree and returns the optimized version.
        ///
        /// This method receives a single syntax tree and should return an optimized
        /// version of it. If no optimizations are applicable, return the original tree.
        /// The optimizer should track what optimizations were applied via the context.
        /// </summary>
        /// <param name="tree">The syntax tree to optimize.</param>
        /// <param name="context">Context for tracking optimizations and accessing compilation-wide state.</param>
        /// <returns>The optimized syntax tree, or the original if no optimizations applied.</returns>
        SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context);
    }
}

