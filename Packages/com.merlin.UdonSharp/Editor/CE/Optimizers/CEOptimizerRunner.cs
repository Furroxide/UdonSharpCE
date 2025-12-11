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
            // Initialize optimizers in priority order
            var optimizerList = new List<ICompileTimeOptimizer>
            {
                // Priority 0-99: Early optimizations
                new AsyncMethodTransformOptimizer(), // Priority 1 - Transform async/await FIRST before anything else
                new ActionToCallbackTransformer(), // Priority 3 - Transform Action delegates to CECallback before other optimizations
                new CELoggerTransformOptimizer(),  // Priority 5 - Transform CELogger calls before other optimizations
                new ConstantFoldingOptimizer(),    // Priority 10

                // Priority 100-199: Standard optimizations
                new DeadCodeEliminationOptimizer(),           // Priority 100
                new LoopInvariantCodeMotionOptimizer(),       // Priority 105 - Hoist invariants before other loop opts
                new SmallLoopUnrollingOptimizer(),            // Priority 110
                new CommonSubexpressionEliminationOptimizer(), // Priority 115 - CSE after loop opts
                new TinyMethodInliningOptimizer(),            // Priority 118
                new ExternCallCachingOptimizer(),             // Priority 120 - Cache extern calls

                // Priority 200+: Late optimizations
                new StringInterningOptimizer(),
            };

            // Sort by priority
            Optimizers = optimizerList.OrderBy(o => o.Priority).ToArray();

            // Register with the compiler
            CompileTimeOptimizerRegistry.RegisterOptimizer(RunOptimizers);
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

            // Clear previous optimization records
            Context.Clear();

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
                    // Log but don't fail compilation for optimizer errors
                    Debug.LogWarning(
                        $"[CE.Optimizers] Optimizer {optimizer.OptimizerId} ({optimizer.OptimizerName}) failed: {ex.Message}");

                    if (_enableDebugLogging)
                        Debug.LogException(ex);
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
                        $"[CE.Optimizers] {optimizer.OptimizerId} failed on {filePath}: {ex.Message}");
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
