using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.CE.Editor.Async;
using UdonSharp.Compiler;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Orchestrates running all CE optimizers during the compilation setup phase.
    ///
    /// This class manages the collection of registered optimizers and executes them
    /// in priority order before Roslyn compilation begins.
    /// </summary>
    [InitializeOnLoad]
    internal static class CEOptimizerRunner
    {
        /// <summary>
        /// The collection of all registered optimizers, sorted by priority.
        /// </summary>
        private static readonly ICompileTimeOptimizer[] Optimizers;

        /// <summary>
        /// The shared optimization context.
        /// </summary>
        private static readonly OptimizationContext Context = new OptimizationContext();

        /// <summary>
        /// Whether to log optimizer execution for debugging.
        /// </summary>
        private static bool _enableDebugLogging = false;

        /// <summary>
        /// Static constructor initializes optimizers and registers with the compiler.
        /// </summary>
        static CEOptimizerRunner()
        {
            // This list holds OPTIMIZATIONS ONLY: passes the program is merely slower without.
            //
            // The correctness-critical code generators -- async transform, lambda inlining,
            // Action-to-CECallback, CELogger rewrite, SyncOnJoin glue, quantization and persistence
            // auto-registration -- live in CEGeneratorRunner and run in an earlier compiler phase where a
            // failure is a hard compile error. They are not optimizations and must never be skippable.
            var optimizerList = new List<ICompileTimeOptimizer>
            {
                // Note: ConstantFoldingOptimizer (CEOPT001) retired - Binder's ConstantExpressionOptimizer handles this
                // Note: DeadCodeEliminationOptimizer (CEOPT002) retired - binary-level passes handle this
                //
                // Every pass below is currently IsEnabledByDefault => false. Each one is a syntactic
                // rewriter that can drop writes, hoist past null guards, or emit uncompilable code; see the
                // comment on each type. They stay registered so the report window and the ID-uniqueness
                // guardrail can still see them, and so re-enabling one is a single-line change once it has
                // been rewritten against the semantic model.
                new LoopInvariantCodeMotionOptimizer(),        // CEOPT010
                new SmallLoopUnrollingOptimizer(),             // CEOPT003
                new CommonSubexpressionEliminationOptimizer(),  // CEOPT009
                new TinyMethodInliningOptimizer(),             // CEOPT004
                new ExternCallCachingOptimizer(),              // CEOPT008
                new StringInterningOptimizer(),                // CEOPT005
            };

            // Sort by priority
            Optimizers = optimizerList.OrderBy(o => o.Priority).ToArray();

            // Register with the compiler
            CompileTimeOptimizerRegistry.RegisterOptimizer(RunOptimizers);
        }

        /// <summary>
        /// The registered optimizers' ids, for the combined uniqueness assertion that
        /// <c>CEGeneratorRunner</c> runs at registration.
        ///
        /// The collision that assertion exists to prevent is not hypothetical: CEOPT009 was claimed by both
        /// the SyncOnJoin glue emitter and common subexpression elimination, and CEOPT010 by both the
        /// quantize emitter and loop-invariant code motion. Because the "contract-critical" escalation set
        /// was keyed by id, an exception in either perf pass was escalated as though a sync generator had
        /// failed, and the report window attributed sync glue emission to "Common Subexpression
        /// Elimination". That collision spans both registries, so only a check that sees the generator set
        /// too can catch it -- and only the generator registry can fail the compile over it.
        /// </summary>
        internal static (string Id, string Name)[] GetRegisteredTransformIds()
        {
            var ids = new (string, string)[Optimizers.Length];

            for (int i = 0; i < Optimizers.Length; i++)
                ids[i] = (Optimizers[i].OptimizerId, Optimizers[i].OptimizerName);

            return ids;
        }

        /// <summary>
        /// The id a registered optimizer records its entries under, looked up by type.
        ///
        /// The inspector's report table used to spell these out by hand ("SmallLoopUnrolling") while the
        /// optimizers record "CEOPT003", so every arm of that table was dead and the report read all zeros.
        /// Sourcing the id from the registered instance makes that class of drift impossible.
        /// </summary>
        internal static string GetOptimizerId<T>() where T : ICompileTimeOptimizer
        {
            foreach (ICompileTimeOptimizer optimizer in Optimizers)
            {
                if (optimizer is T)
                    return optimizer.OptimizerId;
            }

            return null;
        }

        /// <summary>
        /// Runs all enabled optimizers on the given syntax trees.
        ///
        /// This method is called during the Setup phase before Roslyn compilation.
        /// Each optimizer transforms the syntax trees in place, accumulating
        /// optimizations.
        /// </summary>
        /// <param name="syntaxTrees">Array of syntax trees and their file paths.</param>
        /// <returns>Array of optimized syntax trees.</returns>
        public static (SyntaxTree tree, string filePath)[] RunOptimizers(
            (SyntaxTree tree, string filePath)[] syntaxTrees)
        {
            if (syntaxTrees == null || syntaxTrees.Length == 0)
                return syntaxTrees;

            // Deliberately no Context.Clear() here. CEGeneratorRunner runs earlier in the compile and owns
            // the single Clear() per compile, so the optimization report retains both the generator records
            // and the optimizer records instead of only the latter.

            if (!Context.OptimizationsEnabled)
            {
                if (_enableDebugLogging)
                    Debug.Log("[CE.Optimizers] Optimizations disabled, skipping");
                return syntaxTrees;
            }

            var result = new (SyntaxTree tree, string filePath)[syntaxTrees.Length];
            Array.Copy(syntaxTrees, result, syntaxTrees.Length);

            foreach (var optimizer in Optimizers)
            {
                if (!optimizer.IsEnabledByDefault)
                    continue;

                try
                {
                    result = RunSingleOptimizer(optimizer, result);
                }
                catch (Exception ex)
                {
                    // Optimizer failures are genuinely non-fatal: the program is already correct, just
                    // unoptimized. Every correctness-critical transform now lives in CEGeneratorRunner,
                    // where a failure aborts the compile instead.
                    Debug.LogWarning(
                        $"[CE.Optimizers] Optimizer {optimizer.OptimizerId} ({optimizer.OptimizerName}) failed " +
                        $"and was skipped; the program is correct but unoptimized: {ex}");
                }
            }

            // Log summary if we did any optimizations
            if (Context.TotalOptimizations > 0)
            {
                if (_enableDebugLogging)
                {
                    Debug.Log($"[CE.Optimizers] Applied {Context.TotalOptimizations} optimizations");
                    foreach (var (id, count) in Context.GetOptimizationCounts())
                    {
                        Debug.Log($"  {id}: {count}");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Runs a single optimizer on all syntax trees.
        /// </summary>
        private static (SyntaxTree tree, string filePath)[] RunSingleOptimizer(
            ICompileTimeOptimizer optimizer,
            (SyntaxTree tree, string filePath)[] syntaxTrees)
        {
            var result = new (SyntaxTree tree, string filePath)[syntaxTrees.Length];
            Context.SetSemanticModelTrees(syntaxTrees.Select(item => item.tree));

            for (int i = 0; i < syntaxTrees.Length; i++)
            {
                var (tree, filePath) = syntaxTrees[i];
                Context.CurrentFilePath = filePath;

                try
                {
                    var optimizedTree = optimizer.Optimize(tree, Context);
                    result[i] = (optimizedTree, filePath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[CE.Optimizers] {optimizer.OptimizerId} failed on {filePath} and that file was left " +
                        $"unoptimized: {ex}");

                    result[i] = (tree, filePath);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the current optimization context for report generation.
        /// </summary>
        public static OptimizationContext GetContext() => Context;

        /// <summary>
        /// Gets information about all registered optimizers.
        /// </summary>
        public static (string Id, string Name, string Description, bool Enabled, int Priority)[] GetOptimizerInfo()
        {
            var info = new (string, string, string, bool, int)[Optimizers.Length];

            for (int i = 0; i < Optimizers.Length; i++)
            {
                var optimizer = Optimizers[i];
                info[i] = (optimizer.OptimizerId, optimizer.OptimizerName, optimizer.Description,
                    optimizer.IsEnabledByDefault, optimizer.Priority);
            }

            return info;
        }

        /// <summary>
        /// Enables or disables all optimizations globally.
        /// </summary>
        public static void SetOptimizationsEnabled(bool enabled)
        {
            Context.OptimizationsEnabled = enabled;
        }

        /// <summary>
        /// Enables or disables debug logging for optimizer execution.
        /// </summary>
        public static void SetDebugLogging(bool enabled)
        {
            _enableDebugLogging = enabled;
        }
    }
}
