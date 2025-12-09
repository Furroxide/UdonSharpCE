using System;
using System.Collections.Generic;
using System.Linq;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Optimization.Passes;
using UnityEngine;

namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Runs a sequence of optimization passes over the assembly instruction stream.
    /// </summary>
    internal class AssemblyOptimizer
    {
        private const int MaxIterations = 10;

        private readonly AssemblyModule _module;
        private readonly List<IOptimizationPass> _passes;

        public AssemblyOptimizer(AssemblyModule module)
        {
            _module = module;
            _passes = CreateDefaultPasses();
        }

        private List<IOptimizationPass> CreateDefaultPasses()
        {
            List<IOptimizationPass> defaultPasses = new List<IOptimizationPass>
            {
                // Priority 100-199: local optimizations
                new PushPopEliminationPass(),
                new RedundantCopyEliminationPass(),
                new PeepholeOptimizationPass(),

                // Priority 200-299: propagation
                new CopyPropagationPass(),

                // Priority 300-399: control flow cleanup
                new JumpThreadingPass(),
                new DeadCodeEliminationPass(),

                // Priority 400-499: heap cleanup
                new ValueCoalescencePass(),
            };

            HashSet<string> disabledPasses = _module.CompileContext?.Options?.DisabledOptimizationPasses
                                                ?? new HashSet<string>();

            return defaultPasses
                .Where(pass => !disabledPasses.Contains(pass.Name))
                .OrderBy(pass => pass.Priority)
                .ToList();
        }

        public void Optimize()
        {
            OptimizationContext context = new OptimizationContext(_module);
            context.EnsureInstructionAddresses();

            int iteration = 0;
            bool changed;

            do
            {
                changed = false;

                foreach (IOptimizationPass pass in _passes)
                {
                    if (!pass.CanRun(context))
                        continue;

                    try
                    {
                        context.EnsureInstructionAddresses();
                        bool passChanged = pass.Run(context);

                        if (passChanged)
                        {
                            changed = true;
                            context.Metrics.PassesRun++;
                            context.EnsureInstructionAddresses();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[UdonSharp] Optimization pass '{pass.Name}' failed: {ex.Message}");
                    }
                }

                iteration++;
            } while (changed && iteration < MaxIterations);

            _module.RecalculateInstructionAddresses();

#if UDONSHARP_DEBUG
            LogMetrics(context.Metrics);
#endif
        }

        private static void LogMetrics(OptimizationMetrics metrics)
        {
            Debug.Log($"[UdonSharp] Optimization removed {metrics.InstructionsRemoved} instructions, " +
                      $"replaced {metrics.InstructionsReplaced}, " +
                      $"coalesced {metrics.ValuesCoalesced}, " +
                      $"threaded {metrics.JumpsThreaded} jumps, " +
                      $"dead blocks removed {metrics.DeadBlocksRemoved}, " +
                      $"passes run {metrics.PassesRun}");
        }
    }
}
