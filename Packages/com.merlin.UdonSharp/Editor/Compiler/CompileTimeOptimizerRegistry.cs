using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

[assembly: InternalsVisibleTo("UdonSharp.CE.Editor")]

namespace UdonSharp.Compiler
{
    /// <summary>
    /// Registry for compile-time optimizers that can be registered by external assemblies.
    /// This allows CE.Editor to register its optimizers without creating a cyclic dependency.
    /// 
    /// Optimizers transform syntax trees before Roslyn compilation to generate more efficient
    /// Udon code. They run during the Setup phase of compilation.
    /// </summary>
    public static class CompileTimeOptimizerRegistry
    {
        /// <summary>
        /// Delegate signature for optimizer callbacks.
        /// Takes an array of syntax trees and returns optimized versions.
        /// </summary>
        /// <param name="syntaxTrees">Array of (tree, filePath) tuples to optimize.</param>
        /// <returns>Array of optimized (tree, filePath) tuples.</returns>
        internal delegate (SyntaxTree tree, string filePath)[] OptimizerCallback(
            (SyntaxTree tree, string filePath)[] syntaxTrees);

        private static readonly List<OptimizerCallback> _registeredOptimizers = new List<OptimizerCallback>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Registers an optimizer callback to be invoked during compilation.
        /// </summary>
        /// <param name="optimizer">The optimizer callback to register.</param>
        internal static void RegisterOptimizer(OptimizerCallback optimizer)
        {
            if (optimizer == null) return;

            lock (_lock)
            {
                if (!_registeredOptimizers.Contains(optimizer))
                {
                    _registeredOptimizers.Add(optimizer);
                }
            }
        }

        /// <summary>
        /// Unregisters an optimizer callback.
        /// </summary>
        /// <param name="optimizer">The optimizer callback to unregister.</param>
        internal static void UnregisterOptimizer(OptimizerCallback optimizer)
        {
            if (optimizer == null) return;

            lock (_lock)
            {
                _registeredOptimizers.Remove(optimizer);
            }
        }

        /// <summary>
        /// Runs all registered optimizers on the given syntax trees.
        /// Called by the compiler during the Setup phase.
        /// </summary>
        /// <param name="syntaxTrees">Array of syntax trees and their file paths.</param>
        /// <returns>Array of optimized syntax trees.</returns>
        internal static (SyntaxTree tree, string filePath)[] RunOptimizers(
            (SyntaxTree tree, string filePath)[] syntaxTrees)
        {
            OptimizerCallback[] optimizers;

            lock (_lock)
            {
                optimizers = _registeredOptimizers.ToArray();
            }

            var result = syntaxTrees;

            foreach (var optimizer in optimizers)
            {
                try
                {
                    result = optimizer(result);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[UdonSharp] Optimizer failed: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the count of registered optimizers.
        /// </summary>
        public static int OptimizerCount
        {
            get
            {
                lock (_lock)
                {
                    return _registeredOptimizers.Count;
                }
            }
        }
    }
}

