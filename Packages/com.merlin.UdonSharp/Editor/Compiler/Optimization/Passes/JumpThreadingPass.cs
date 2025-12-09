using System.Collections.Generic;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Optimization;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Collapses jump-to-jump chains into direct jumps.
    /// </summary>
    internal class JumpThreadingPass : IOptimizationPass
    {
        public string Name => "Jump Threading";
        public int Priority => 300;

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            context.EnsureInstructionAddresses();

            bool changed = false;
            var instructions = context.Instructions;
            Dictionary<uint, int> addressToIndex = new Dictionary<uint, int>();

            for (int i = 0; i < instructions.Count; i++)
                addressToIndex[instructions[i].InstructionAddress] = i;

            for (int i = 0; i < instructions.Count; i++)
            {
                JumpLabel targetLabel = null;

                if (instructions[i] is JumpInstruction jump)
                    targetLabel = jump.JumpTarget;
                else if (instructions[i] is JumpIfFalseInstruction jumpIf)
                    targetLabel = jumpIf.JumpTarget;
                else
                    continue;

                if (!addressToIndex.TryGetValue(targetLabel.Address, out int targetIndex))
                    continue;

                if (instructions[targetIndex] is JumpInstruction targetJump)
                {
                    JumpLabel finalTarget = ResolveJumpChain(instructions, addressToIndex, targetJump.JumpTarget, 10);

                    if (finalTarget != targetLabel)
                    {
                        if (instructions[i] is JumpInstruction)
                            context.ReplaceInstruction(i, new JumpInstruction(finalTarget));
                        else if (instructions[i] is JumpIfFalseInstruction conditional)
                            context.ReplaceInstruction(i, new JumpIfFalseInstruction(finalTarget, conditional.ConditionValue));

                        changed = true;
                        context.Metrics.JumpsThreaded++;
                    }
                }
            }

            return changed;
        }

        private JumpLabel ResolveJumpChain(
            List<AssemblyInstruction> instructions,
            Dictionary<uint, int> addressToIndex,
            JumpLabel label,
            int maxDepth)
        {
            if (maxDepth <= 0)
                return label;

            if (!addressToIndex.TryGetValue(label.Address, out int index))
                return label;

            if (instructions[index] is JumpInstruction jump)
                return ResolveJumpChain(instructions, addressToIndex, jump.JumpTarget, maxDepth - 1);

            return label;
        }
    }
}
