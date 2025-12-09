using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Optimization;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Removes immediate PUSH/POP pairs that have no observable effect.
    /// </summary>
    internal class PushPopEliminationPass : IOptimizationPass
    {
        public string Name => "Push-Pop Elimination";
        public int Priority => 100;

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            bool changed = false;
            var instructions = context.Instructions;

            for (int i = instructions.Count - 2; i >= 0; i--)
            {
                if (instructions[i] is PushInstruction &&
                    instructions[i + 1] is PopInstruction)
                {
                    context.RemoveInstruction(i + 1);
                    context.RemoveInstruction(i);
                    changed = true;
                    context.Metrics.RecordPassMetric(Name, "PairsRemoved", 1);
                }
            }

            return changed;
        }
    }
}
