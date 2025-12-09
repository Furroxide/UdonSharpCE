using System.Collections.Generic;
using System.Linq;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Optimization;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Propagates copied values forward to later uses before the next redefinition.
    /// Conservative by design to avoid semantic changes.
    /// </summary>
    internal class CopyPropagationPass : IOptimizationPass
    {
        public string Name => "Copy Propagation";
        public int Priority => 200;

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            bool changed = false;
            var instructions = context.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is not CopyInstruction copy)
                    continue;

                Value source = copy.SourceValue;
                Value target = copy.TargetValue;

                HashSet<int> uses = context.GetValueUses(target);
                HashSet<int> defs = context.GetValueDefs(target);

                List<int> futureDefs = defs.Where(d => d > i).ToList();
                int nextDef = futureDefs.Count == 0 ? int.MaxValue : futureDefs.Min();

                List<int> forwardUses = uses.Where(u => u > i && u < nextDef).ToList();
                if (forwardUses.Count == 0)
                    continue;

                foreach (int useIndex in forwardUses)
                {
                    AssemblyInstruction instruction = context.Instructions[useIndex];
                    AssemblyInstruction newInstruction = RewriteUse(instruction, target, source);

                    if (newInstruction != null)
                    {
                        context.ReplaceInstruction(useIndex, newInstruction);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private AssemblyInstruction RewriteUse(AssemblyInstruction instruction, Value target, Value source)
        {
            switch (instruction)
            {
                case PushInstruction push when push.PushValue == target:
                    return new PushInstruction(source);
                case CopyInstruction copy:
                {
                    Value newSource = copy.SourceValue == target ? source : copy.SourceValue;
                    Value newTarget = copy.TargetValue;

                    if (newSource != copy.SourceValue)
                        return new CopyInstruction(newSource, newTarget);

                    break;
                }
                case JumpIfFalseInstruction jumpIf when jumpIf.ConditionValue == target:
                    return new JumpIfFalseInstruction(jumpIf.JumpTarget, source);
                case JumpIndirectInstruction jumpIndirect when jumpIndirect.JumpTargetValue == target:
                    return new JumpIndirectInstruction(source);
                case RetInstruction ret when ret.RetValRef == target:
                    return new RetInstruction(source);
            }

            return null;
        }
    }
}
