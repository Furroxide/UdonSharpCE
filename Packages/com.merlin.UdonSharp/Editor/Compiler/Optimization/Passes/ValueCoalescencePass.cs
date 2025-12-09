using System.Collections.Generic;
using System.Linq;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Optimization;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Reuses heap slots for non-overlapping locals to reduce heap size.
    /// </summary>
    internal class ValueCoalescencePass : IOptimizationPass
    {
        public string Name => "Value Coalescence";
        public int Priority => 400;

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            List<Value> candidateValues = context.RootValueTable.GetAllUniqueChildValues()
                .Where(v => !v.IsConstant &&
                            !v.IsParameter &&
                            (v.IsLocal || v.IsInternal) &&
                            v.AssociatedSymbol is not FieldSymbol)
                .ToList();

            if (candidateValues.Count < 2)
                return false;

            Dictionary<Value, (int FirstUse, int LastUse)> liveness = new Dictionary<Value, (int, int)>();

            foreach (Value value in candidateValues)
            {
                HashSet<int> uses = context.GetValueUses(value);
                HashSet<int> defs = context.GetValueDefs(value);

                List<int> refs = uses.Union(defs).ToList();
                if (refs.Count == 0)
                    continue;

                liveness[value] = (refs.Min(), refs.Max());
            }

            if (liveness.Count < 2)
                return false;

            List<List<Value>> coalesceGroups = new List<List<Value>>();
            HashSet<Value> assigned = new HashSet<Value>();

            foreach (Value value in liveness.Keys.OrderByDescending(v => liveness[v].LastUse - liveness[v].FirstUse))
            {
                if (assigned.Contains(value))
                    continue;

                List<Value> group = new List<Value> { value };
                assigned.Add(value);

                foreach (Value candidate in liveness.Keys.Where(v => !assigned.Contains(v)))
                {
                    if (candidate.UdonType != value.UdonType)
                        continue;

                    bool overlaps = group.Any(member =>
                    {
                        (int firstUse, int lastUse) memberLife = liveness[member];
                        (int firstUse, int lastUse) candidateLife = liveness[candidate];

                        return !(candidateLife.lastUse < memberLife.firstUse ||
                                 memberLife.lastUse < candidateLife.firstUse);
                    });

                    if (!overlaps)
                    {
                        group.Add(candidate);
                        assigned.Add(candidate);
                    }
                }

                if (group.Count > 1)
                    coalesceGroups.Add(group);
            }

            bool changed = false;

            foreach (List<Value> group in coalesceGroups)
            {
                Value primary = group[0];

                foreach (Value secondary in group.Skip(1))
                {
                    RewriteValueReferences(context, secondary, primary);
                    context.Metrics.ValuesCoalesced++;
                    changed = true;
                }
            }

            return changed;
        }

        private void RewriteValueReferences(OptimizationContext context, Value from, Value to)
        {
            for (int i = 0; i < context.Instructions.Count; i++)
            {
                var instruction = context.Instructions[i];
                switch (instruction)
                {
                    case PushInstruction push when push.PushValue == from:
                        context.ReplaceInstruction(i, new PushInstruction(to));
                        break;
                    case CopyInstruction copy:
                        Value newSource = copy.SourceValue == from ? to : copy.SourceValue;
                        Value newTarget = copy.TargetValue == from ? to : copy.TargetValue;
                        if (newSource != copy.SourceValue || newTarget != copy.TargetValue)
                            context.ReplaceInstruction(i, new CopyInstruction(newSource, newTarget));
                        break;
                    case JumpIfFalseInstruction jumpIf when jumpIf.ConditionValue == from:
                        context.ReplaceInstruction(i, new JumpIfFalseInstruction(jumpIf.JumpTarget, to));
                        break;
                    case JumpIndirectInstruction jumpIndirect when jumpIndirect.JumpTargetValue == from:
                        context.ReplaceInstruction(i, new JumpIndirectInstruction(to));
                        break;
                    case RetInstruction ret when ret.RetValRef == from:
                        context.ReplaceInstruction(i, new RetInstruction(to));
                        break;
                }
            }
        }
    }
}
