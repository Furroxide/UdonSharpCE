using System.Collections.Generic;
using UdonSharp.Compiler.Optimization;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Removes unreachable basic blocks discovered via CFG walk.
    /// </summary>
    internal class DeadCodeEliminationPass : IOptimizationPass
    {
        public string Name => "Dead Code Elimination";
        public int Priority => 310;

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            ControlFlowGraph cfg = context.CFG;

            if (cfg.EntryBlock == null)
                return false;

            HashSet<BasicBlock> reachable = new HashSet<BasicBlock>();
            Queue<BasicBlock> worklist = new Queue<BasicBlock>();

            worklist.Enqueue(cfg.EntryBlock);
            foreach (BasicBlock exportBlock in cfg.ExportBlocks)
                worklist.Enqueue(exportBlock);

            while (worklist.Count > 0)
            {
                BasicBlock block = worklist.Dequeue();

                if (!reachable.Add(block))
                    continue;

                foreach (BasicBlock successor in block.Successors)
                    worklist.Enqueue(successor);
            }

            List<int> unreachableIndices = new List<int>();
            foreach (BasicBlock block in cfg.AllBlocks)
            {
                if (reachable.Contains(block))
                    continue;

                for (int i = block.StartIndex; i <= block.EndIndex; i++)
                    unreachableIndices.Add(i);
            }

            if (unreachableIndices.Count == 0)
                return false;

            unreachableIndices.Sort();
            for (int i = unreachableIndices.Count - 1; i >= 0; i--)
            {
                context.RemoveInstruction(unreachableIndices[i]);
                context.Metrics.DeadBlocksRemoved++;
            }

            return true;
        }
    }
}
