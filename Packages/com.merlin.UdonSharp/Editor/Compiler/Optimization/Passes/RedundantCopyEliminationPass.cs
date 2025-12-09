using System.Linq;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Optimization;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Eliminates copies that do not affect program semantics.
    /// </summary>
    internal class RedundantCopyEliminationPass : IOptimizationPass
    {
        public string Name => "Redundant Copy Elimination";
        public int Priority => 110;

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            bool changed = false;
            var instructions = context.Instructions;

            // Pattern 1: self-copy
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i] is CopyInstruction copy &&
                    copy.SourceValue == copy.TargetValue)
                {
                    context.RemoveInstruction(i);
                    changed = true;
                    context.Metrics.RecordPassMetric(Name, "SelfCopiesRemoved", 1);
                }
            }

            // Pattern 2: dead copy overwritten before use.
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is CopyInstruction copy)
                {
                    var targetUses = context.GetValueUses(copy.TargetValue);
                    var targetDefs = context.GetValueDefs(copy.TargetValue);

                    int nextUse = targetUses.Where(u => u > i).DefaultIfEmpty(int.MaxValue).Min();
                    int nextDef = targetDefs.Where(d => d > i).DefaultIfEmpty(int.MaxValue).Min();

                    if (nextDef < nextUse && !IsValueExported(copy.TargetValue))
                    {
                        context.RemoveInstruction(i);
                        changed = true;
                        i--;
                        context.Metrics.RecordPassMetric(Name, "DeadCopiesRemoved", 1);
                    }
                }
            }

            // Pattern 3: copy chains COPY A->B then B->C, B used nowhere else.
            changed |= OptimizeCopyChains(context);

            return changed;
        }

        private bool OptimizeCopyChains(OptimizationContext context)
        {
            bool changed = false;
            var instructions = context.Instructions;

            for (int i = 0; i < instructions.Count - 1; i++)
            {
                if (instructions[i] is CopyInstruction first &&
                    instructions[i + 1] is CopyInstruction second &&
                    first.TargetValue == second.SourceValue)
                {
                    var uses = context.GetValueUses(first.TargetValue);

                    if (uses.Count == 1 && uses.Contains(i + 1))
                    {
                        CopyInstruction newCopy = new CopyInstruction(first.SourceValue, second.TargetValue);
                        context.ReplaceInstruction(i, newCopy);
                        context.RemoveInstruction(i + 1);
                        changed = true;
                        context.Metrics.RecordPassMetric(Name, "ChainsOptimized", 1);
                    }
                }
            }

            return changed;
        }

        private static bool IsValueExported(Value value)
        {
            if (value?.AssociatedSymbol is FieldSymbol fieldSymbol)
                return fieldSymbol.IsSerialized || fieldSymbol.IsSynced;

            return false;
        }
    }
}
