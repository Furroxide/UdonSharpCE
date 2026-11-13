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
    ///
    /// NOT WIRED IN, deliberately. Nothing constructs this type: <c>AssemblyModule.BuildUasmStr</c> has no
    /// hook for it and <c>UdonSharpCompileOptions.EnableAssemblyOptimization</c> is read by nobody. Do not
    /// add the hook until the problems below are fixed -- each one miscompiles ordinary code on its own.
    ///
    /// 1. Addresses are baked in as data. <c>JumpLabel.Address</c> is a raw uint assigned once, and call
    ///    return addresses (<c>BoundUserMethodInvocation</c>) and switch jump tables
    ///    (<c>BoundSwitchStatement</c>) are plain uint constants on the heap.
    ///    <c>RecalculateInstructionAddresses</c> only renumbers instructions, so removing a single
    ///    instruction leaves every later jump target, return address and jump-table entry pointing at the
    ///    wrong place. Labels need to be bound to instructions and resolved after the last pass.
    /// 2. The control-flow graph has no notion of indirect control flow. <c>RET</c> and
    ///    <c>JUMP_INDIRECT</c> do not end a block, and return points and jump-table targets are not
    ///    leaders or roots. Dead-code elimination therefore deletes the code after every user-method
    ///    call (it is reached only through the callee's <c>JUMP_INDIRECT</c>), and can take an
    ///    <c>ExportTag</c> with it, un-exporting the event that follows.
    /// 3. Liveness is decided by instruction index, not control flow. <c>RedundantCopyEliminationPass</c>
    ///    drops <c>x = a</c> in <c>x = a; if (c) x = b; Use(x);</c> because the next textual reference
    ///    to x is another copy; <c>CopyPropagationPass</c> rewrites a loop counter's reads to the constant
    ///    it was initialised from; <c>ValueCoalescencePass</c> merges slots that are live across a call,
    ///    including the per-call return-address constants.
    /// 4. Extern writes are invisible. <c>OptimizationContext</c> records only <c>COPY</c> targets as
    ///    definitions, but an extern writes its output operand (and any ref/out operand) through a
    ///    <c>PUSH</c>, which is modelled as a read.
    /// 5. <c>ExternDeduplicationPass</c> treats any extern whose signature contains "__get_", "Vector",
    ///    "Quaternion", "Color" or "Math" as pure, which covers <c>Random.value</c>,
    ///    <c>transform.position</c> and <c>Rigidbody.AddForce(Vector3)</c>, and never invalidates a cached
    ///    result when an argument is written.
    /// 6. <c>StrengthReductionPass</c> replaces the <c>EXTERN</c> with a <c>COPY</c> but leaves the three
    ///    operand <c>PUSH</c>es behind, so the stack is unbalanced when the method returns.
    /// 7. Debug info (<c>UdonSharpClassDebugInfo</c>) records addresses during emit and is never remapped,
    ///    so runtime exception line numbers would drift after any removal.
    ///
    /// The types stay in the tree as a starting point, not as a feature.
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
                new StrengthReductionPass(),  // Priority 125: replace expensive ops with cheaper equivalents
                new ExternDeduplicationPass(), // Priority 130: CSE for extern calls

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
                        UdonSharpCEUtils.LogWarning($"Optimization pass '{pass.Name}' failed: {ex.Message}");
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
            UdonSharpCEUtils.Log($"Optimization removed {metrics.InstructionsRemoved} instructions, " +
                      $"replaced {metrics.InstructionsReplaced}, " +
                      $"coalesced {metrics.ValuesCoalesced}, " +
                      $"threaded {metrics.JumpsThreaded} jumps, " +
                      $"dead blocks removed {metrics.DeadBlocksRemoved}, " +
                      $"passes run {metrics.PassesRun}");
        }
    }
}
