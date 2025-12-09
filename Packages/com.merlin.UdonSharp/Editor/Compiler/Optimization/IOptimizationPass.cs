using UdonSharp.Compiler.Assembly;

namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Interface for assembly optimization passes executed by <see cref="AssemblyOptimizer"/>.
    /// </summary>
    internal interface IOptimizationPass
    {
        /// <summary>
        /// Human readable name used for diagnostics and opt-out lists.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Lower numbers are executed earlier.
        /// Recommended ranges:
        /// 0-99 analysis, 100-199 local opts, 200-299 propagation,
        /// 300-399 elimination, 400-499 cleanup.
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Whether this pass should run for the current context.
        /// </summary>
        bool CanRun(OptimizationContext context);

        /// <summary>
        /// Execute the optimization pass.
        /// </summary>
        /// <returns>True if any modifications were made.</returns>
        bool Run(OptimizationContext context);
    }
}
