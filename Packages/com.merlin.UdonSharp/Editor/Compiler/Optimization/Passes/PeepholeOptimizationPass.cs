using System.Collections.Generic;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Optimization;

namespace UdonSharp.Compiler.Optimization.Passes
{
    internal class PeepholeOptimizationPass : IOptimizationPass
    {
        public string Name => "Peephole Optimization";
        public int Priority => 120;

        private readonly List<PeepholePattern> _patterns = new List<PeepholePattern>
        {
            new BooleanConstantJumpPattern(),
            new ConsecutivePushPattern(),
            new NopRemovalPattern(),
        };

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            bool changed = false;

            foreach (PeepholePattern pattern in _patterns)
            {
                changed |= pattern.Apply(context);
            }

            return changed;
        }
    }

    internal abstract class PeepholePattern
    {
        public abstract bool Apply(OptimizationContext context);
    }

    internal class BooleanConstantJumpPattern : PeepholePattern
    {
        public override bool Apply(OptimizationContext context)
        {
            bool changed = false;
            var instructions = context.Instructions;

            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i] is JumpIfFalseInstruction jumpIf &&
                    jumpIf.ConditionValue.IsConstant &&
                    jumpIf.ConditionValue.DefaultValue is bool constantValue)
                {
                    if (constantValue)
                    {
                        context.RemoveInstruction(i);
                        changed = true;
                    }
                    else
                    {
                        JumpInstruction replacement = new JumpInstruction(jumpIf.JumpTarget);
                        context.ReplaceInstruction(i, replacement);
                        changed = true;
                    }
                }
            }

            return changed;
        }
    }

    internal class ConsecutivePushPattern : PeepholePattern
    {
        public override bool Apply(OptimizationContext context)
        {
            // Placeholder for future safe push pattern optimizations.
            return false;
        }
    }

    internal class NopRemovalPattern : PeepholePattern
    {
        public override bool Apply(OptimizationContext context)
        {
            bool changed = false;
            var instructions = context.Instructions;

            context.EnsureInstructionAddresses();

            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i] is NopInstruction)
                {
                    if (!IsJumpTarget(context, instructions[i].InstructionAddress))
                    {
                        context.RemoveInstruction(i);
                        changed = true;
                        context.EnsureInstructionAddresses();
                    }
                }
            }

            return changed;
        }

        private static bool IsJumpTarget(OptimizationContext context, uint address)
        {
            foreach (var instruction in context.Instructions)
            {
                if (instruction is JumpInstruction jump && jump.JumpTarget.Address == address)
                    return true;

                if (instruction is JumpIfFalseInstruction jumpIf && jumpIf.JumpTarget.Address == address)
                    return true;
            }

            return false;
        }
    }
}
