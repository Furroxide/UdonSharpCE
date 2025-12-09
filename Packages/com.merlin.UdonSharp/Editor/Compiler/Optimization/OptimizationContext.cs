using System.Collections.Generic;
using System.Linq;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Emit;

namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Shared mutable state used by optimization passes.
    /// </summary>
    internal class OptimizationContext
    {
        public AssemblyModule Module { get; }
        public List<AssemblyInstruction> Instructions { get; }
        public ValueTable RootValueTable { get; }
        public CompilationContext CompileContext { get; }
        public OptimizationMetrics Metrics { get; }

        private ControlFlowGraph _cfg;
        private Dictionary<Value, HashSet<int>> _valueUses;
        private Dictionary<Value, HashSet<int>> _valueDefs;
        private bool _addressesDirty;

        public ControlFlowGraph CFG
        {
            get
            {
                EnsureInstructionAddresses();
                return _cfg ??= BuildCFG();
            }
        }

        public OptimizationContext(AssemblyModule module)
        {
            Module = module;
            Instructions = module.GetInstructionList();
            RootValueTable = module.RootTable;
            CompileContext = module.CompileContext;
            Metrics = new OptimizationMetrics();
        }

        /// <summary>
        /// Gets instruction indices where a value is used (read).
        /// </summary>
        public HashSet<int> GetValueUses(Value value)
        {
            EnsureUseDefAnalysis();
            return _valueUses.TryGetValue(value, out HashSet<int> uses) ? uses : new HashSet<int>();
        }

        /// <summary>
        /// Gets instruction indices where a value is defined (written).
        /// </summary>
        public HashSet<int> GetValueDefs(Value value)
        {
            EnsureUseDefAnalysis();
            return _valueDefs.TryGetValue(value, out HashSet<int> defs) ? defs : new HashSet<int>();
        }

        /// <summary>
        /// Marks cached analysis as dirty.
        /// </summary>
        public void InvalidateAnalysis()
        {
            _valueUses = null;
            _valueDefs = null;
            _cfg = null;
            _addressesDirty = true;
        }

        /// <summary>
        /// Removes an instruction and marks dependent analysis as dirty.
        /// </summary>
        public void RemoveInstruction(int index)
        {
            Metrics.InstructionsRemoved++;
            Instructions.RemoveAt(index);
            InvalidateAnalysis();
        }

        /// <summary>
        /// Replaces an instruction at the given index.
        /// </summary>
        public void ReplaceInstruction(int index, AssemblyInstruction newInstruction)
        {
            Metrics.InstructionsReplaced++;

            uint oldAddress = Instructions[index].InstructionAddress;
            newInstruction.InstructionAddress = oldAddress;

            Instructions[index] = newInstruction;
            InvalidateAnalysis();
        }

        /// <summary>
        /// Ensures instruction addresses and jump labels are up to date.
        /// </summary>
        public void EnsureInstructionAddresses()
        {
            if (!_addressesDirty)
                return;

            Module.RecalculateInstructionAddresses();
            _addressesDirty = false;
        }

        private void EnsureUseDefAnalysis()
        {
            if (_valueUses != null)
                return;

            EnsureInstructionAddresses();

            _valueUses = new Dictionary<Value, HashSet<int>>();
            _valueDefs = new Dictionary<Value, HashSet<int>>();

            for (int i = 0; i < Instructions.Count; i++)
            {
                AssemblyInstruction instruction = Instructions[i];

                foreach (Value usedValue in GetUsedValues(instruction))
                {
                    if (!_valueUses.ContainsKey(usedValue))
                        _valueUses[usedValue] = new HashSet<int>();

                    _valueUses[usedValue].Add(i);
                }

                foreach (Value definedValue in GetDefinedValues(instruction))
                {
                    if (!_valueDefs.ContainsKey(definedValue))
                        _valueDefs[definedValue] = new HashSet<int>();

                    _valueDefs[definedValue].Add(i);
                }
            }
        }

        private IEnumerable<Value> GetUsedValues(AssemblyInstruction instruction)
        {
            switch (instruction)
            {
                case PushInstruction push:
                    yield return push.PushValue;
                    break;
                case CopyInstruction copy:
                    yield return copy.SourceValue;
                    break;
                case JumpIfFalseInstruction jumpIf:
                    yield return jumpIf.ConditionValue;
                    break;
                case JumpIndirectInstruction jumpIndirect:
                    yield return jumpIndirect.JumpTargetValue;
                    break;
                case RetInstruction ret:
                    yield return ret.RetValRef;
                    break;
            }
        }

        private IEnumerable<Value> GetDefinedValues(AssemblyInstruction instruction)
        {
            switch (instruction)
            {
                case CopyInstruction copy:
                    yield return copy.TargetValue;
                    break;
            }
        }

        private ControlFlowGraph BuildCFG()
        {
            EnsureInstructionAddresses();
            return new ControlFlowGraph(Instructions);
        }
    }
}
