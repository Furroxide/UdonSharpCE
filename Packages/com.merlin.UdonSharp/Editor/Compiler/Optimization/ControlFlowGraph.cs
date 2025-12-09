using System.Collections.Generic;
using System.Linq;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Assembly.Instructions;

namespace UdonSharp.Compiler.Optimization
{
    internal class BasicBlock
    {
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public List<BasicBlock> Successors { get; } = new List<BasicBlock>();
        public List<BasicBlock> Predecessors { get; } = new List<BasicBlock>();
        public bool IsExportEntry { get; set; }
    }

    internal class ControlFlowGraph
    {
        public BasicBlock EntryBlock { get; private set; }
        public List<BasicBlock> ExportBlocks { get; } = new List<BasicBlock>();
        public List<BasicBlock> AllBlocks { get; } = new List<BasicBlock>();

        public ControlFlowGraph(List<AssemblyInstruction> instructions)
        {
            Build(instructions);
        }

        private void Build(List<AssemblyInstruction> instructions)
        {
            if (instructions.Count == 0)
                return;

            // Step 1: identify basic block leaders.
            HashSet<int> leaders = new HashSet<int> { 0 };

            for (int i = 0; i < instructions.Count; i++)
            {
                AssemblyInstruction instruction = instructions[i];

                if (instruction is JumpInstruction jump)
                {
                    int targetIndex = FindInstructionIndex(instructions, jump.JumpTarget.Address);
                    if (targetIndex >= 0)
                        leaders.Add(targetIndex);

                    if (i + 1 < instructions.Count)
                        leaders.Add(i + 1);
                }
                else if (instruction is JumpIfFalseInstruction jumpIf)
                {
                    int targetIndex = FindInstructionIndex(instructions, jumpIf.JumpTarget.Address);
                    if (targetIndex >= 0)
                        leaders.Add(targetIndex);

                    if (i + 1 < instructions.Count)
                        leaders.Add(i + 1);
                }
                else if (instruction is ExportTag)
                {
                    if (i + 1 < instructions.Count)
                        leaders.Add(i + 1);
                }
            }

            // Step 2: create blocks from sorted leaders.
            List<int> leaderList = leaders.OrderBy(l => l).ToList();
            Dictionary<int, BasicBlock> indexToBlock = new Dictionary<int, BasicBlock>();

            for (int i = 0; i < leaderList.Count; i++)
            {
                int start = leaderList[i];
                int end = (i + 1 < leaderList.Count) ? leaderList[i + 1] - 1 : instructions.Count - 1;

                BasicBlock block = new BasicBlock
                {
                    StartIndex = start,
                    EndIndex = end,
                };

                AllBlocks.Add(block);
                indexToBlock[start] = block;

                if (start > 0 && instructions[start - 1] is ExportTag)
                {
                    block.IsExportEntry = true;
                    ExportBlocks.Add(block);
                }
            }

            EntryBlock = AllBlocks.FirstOrDefault();

            // Step 3: wire up successors and predecessors.
            foreach (BasicBlock block in AllBlocks)
            {
                AssemblyInstruction lastInstruction = instructions[block.EndIndex];

                if (lastInstruction is JumpInstruction jump)
                {
                    int targetIndex = FindInstructionIndex(instructions, jump.JumpTarget.Address);
                    if (indexToBlock.TryGetValue(targetIndex, out BasicBlock target))
                    {
                        block.Successors.Add(target);
                        target.Predecessors.Add(block);
                    }
                }
                else if (lastInstruction is JumpIfFalseInstruction jumpIf)
                {
                    int targetIndex = FindInstructionIndex(instructions, jumpIf.JumpTarget.Address);
                    if (indexToBlock.TryGetValue(targetIndex, out BasicBlock target))
                    {
                        block.Successors.Add(target);
                        target.Predecessors.Add(block);
                    }

                    int fallthroughIndex = block.EndIndex + 1;
                    if (indexToBlock.TryGetValue(fallthroughIndex, out BasicBlock fallthrough))
                    {
                        block.Successors.Add(fallthrough);
                        fallthrough.Predecessors.Add(block);
                    }
                }
                else if (lastInstruction is RetInstruction)
                {
                    // Terminal block, no outgoing edge.
                }
                else
                {
                    int nextIndex = block.EndIndex + 1;
                    if (indexToBlock.TryGetValue(nextIndex, out BasicBlock next))
                    {
                        block.Successors.Add(next);
                        next.Predecessors.Add(block);
                    }
                }
            }
        }

        private int FindInstructionIndex(List<AssemblyInstruction> instructions, uint address)
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].InstructionAddress == address)
                    return i;
            }

            return -1;
        }
    }
}
