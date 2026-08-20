using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// Bridge between compile-time optimization data and the inspector.
    /// Queries CEOptimizerRunner for optimization entries and generates
    /// per-behaviour reports for display.
    /// </summary>
    [InitializeOnLoad]
    public static class CEOptimizationRegistry
    {
        // Cache of optimization reports keyed by source file path
        private static readonly Dictionary<string, CEOptimizationReport> _reportCache = 
            new Dictionary<string, CEOptimizationReport>();

        // Last compilation time for cache invalidation
        private static DateTime _lastCacheTime = DateTime.MinValue;

        /// <summary>
        /// Maps a recorded optimizer id to the report counter it feeds.
        ///
        /// The ids are sourced from the runner instead of being spelled out here. The table this replaces
        /// matched names like "SmallLoopUnrolling" while the optimizers record "CEOPT003", so no arm could
        /// ever match and every report -- including the derived InstructionReduction -- was a permanent
        /// zero. Arms for constant folding and dead-code elimination are gone with those retired
        /// optimizers, and the sync-packing/delta-sync arms named passes that never existed.
        ///
        /// Correcting the ids alone was not enough to make the table populate: all three optimizer arms
        /// below are <c>IsEnabledByDefault =&gt; false</c> and <c>CEOptimizerRunner.RunOptimizers</c> skips
        /// disabled passes, so they record nothing today and the report stayed at zero for a second reason.
        /// The lambda-inlining arm is what makes it live -- that pass runs from CEGeneratorRunner on every
        /// compile regardless of the enable flag. The three dormant arms stay because they are keyed off the
        /// registered instance, so re-enabling a pass re-arms its counter in the same change rather than
        /// leaving a silently zero row behind.
        /// </summary>
        private static readonly Dictionary<string, Action<CEOptimizationReport>> _reportCounters =
            BuildReportCounters();

        private static Dictionary<string, Action<CEOptimizationReport>> BuildReportCounters()
        {
            var counters = new Dictionary<string, Action<CEOptimizationReport>>(StringComparer.Ordinal);

            // Dormant while these passes are disabled -- see the class comment.
            AddOptimizerCounter<SmallLoopUnrollingOptimizer>(counters, report => report.LoopsUnrolled++);
            AddOptimizerCounter<TinyMethodInliningOptimizer>(counters, report => report.MethodsInlined++);
            AddOptimizerCounter<StringInterningOptimizer>(counters, report => report.StringsInterned++);

            // Runs on every compile, so this is the arm that actually populates the inspector panel.
            // It reports under MethodsInlined because that is literally what it does -- the lambda body is
            // spliced into its call site -- and the report's field set is fixed by CEInspectorDataTypes,
            // which the renderer switches on field by field.
            AddGeneratorCounter<LambdaInliningOptimizer>(counters, report => report.MethodsInlined++);

            return counters;
        }

        private static void AddOptimizerCounter<T>(
            Dictionary<string, Action<CEOptimizationReport>> counters,
            Action<CEOptimizationReport> counter)
            where T : ICompileTimeOptimizer
        {
            string optimizerId = CEOptimizerRunner.GetOptimizerId<T>();

            // Null once an optimizer is retired from the runner. The arm then disappears rather than
            // resurrecting an id nothing records under any more.
            if (!string.IsNullOrEmpty(optimizerId))
                counters[optimizerId] = counter;
        }

        /// <summary>
        /// Same single-sourcing for a transform that lives in <c>CEGeneratorRunner</c> rather than
        /// <c>CEOptimizerRunner</c>. Its id is read off a throwaway instance instead of being spelled out,
        /// because a hand-written "CEOPT011" here is exactly the drift that left this whole table dead:
        /// the ids in the report table and the ids the transforms record under have to move together.
        /// </summary>
        private static void AddGeneratorCounter<T>(
            Dictionary<string, Action<CEOptimizationReport>> counters,
            Action<CEOptimizationReport> counter)
            where T : ICompileTimeOptimizer, new()
        {
            string optimizerId = new T().OptimizerId;

            if (!string.IsNullOrEmpty(optimizerId))
                counters[optimizerId] = counter;
        }

        static CEOptimizationRegistry()
        {
            Initialize();
        }

        /// <summary>
        /// Initialize the registry. Called on domain reload.
        /// </summary>
        public static void Initialize()
        {
            // Clear cache on initialization
            _reportCache.Clear();
            _lastCacheTime = DateTime.MinValue;
        }

        /// <summary>
        /// Gets the optimization report for a given target object.
        /// </summary>
        /// <param name="target">The UdonSharpBehaviour or other target object.</param>
        /// <returns>The optimization report, or null if no optimizations were applied.</returns>
        public static CEOptimizationReport GetReport(Object target)
        {
            if (target == null)
                return null;

            // Try to get the source file path for this target
            string sourceFilePath = GetSourceFilePath(target);
            if (string.IsNullOrEmpty(sourceFilePath))
                return null;

            return GetReportForFile(sourceFilePath);
        }

        /// <summary>
        /// Gets the optimization report for a given source file path.
        /// </summary>
        /// <param name="filePath">The source file path.</param>
        /// <returns>The optimization report, or null if no optimizations were applied.</returns>
        public static CEOptimizationReport GetReportForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            // Normalize path
            filePath = NormalizePath(filePath);

            // Check if cache needs refreshing
            RefreshCacheIfNeeded();

            // Return cached report if available
            if (_reportCache.TryGetValue(filePath, out var cachedReport))
                return cachedReport;

            // Generate report from current optimization context
            var report = GenerateReportForFile(filePath);
            
            if (report != null)
                _reportCache[filePath] = report;

            return report;
        }

        /// <summary>
        /// Clears the report cache. Call after recompilation.
        /// </summary>
        public static void ClearCache()
        {
            _reportCache.Clear();
            _lastCacheTime = DateTime.MinValue;
        }

        /// <summary>
        /// Gets the source file path for a target object.
        /// </summary>
        private static string GetSourceFilePath(Object target)
        {
            try
            {
                // Handle UdonSharpBehaviour
                if (target is UdonSharpBehaviour usb)
                {
                    var programAsset = UdonSharpEditorUtility.GetUdonSharpProgramAsset(usb);
                    if (programAsset != null && programAsset.sourceCsScript != null)
                    {
                        return AssetDatabase.GetAssetPath(programAsset.sourceCsScript);
                    }
                }

                // Handle MonoScript directly
                if (target is MonoScript monoScript)
                {
                    return AssetDatabase.GetAssetPath(monoScript);
                }

                // Handle UdonSharpProgramAsset
                if (target is UdonSharpProgramAsset programAsset2)
                {
                    if (programAsset2.sourceCsScript != null)
                    {
                        return AssetDatabase.GetAssetPath(programAsset2.sourceCsScript);
                    }
                }

                // Try to get MonoScript from MonoBehaviour
                if (target is MonoBehaviour mb)
                {
                    var script = MonoScript.FromMonoBehaviour(mb);
                    if (script != null)
                    {
                        return AssetDatabase.GetAssetPath(script);
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Refresh cache if optimization context has changed.
        /// </summary>
        private static void RefreshCacheIfNeeded()
        {
            // Simple time-based invalidation for now
            // Could be improved with compilation event hooks
            var context = GetOptimizationContext();
            if (context == null)
                return;

            // If context has been cleared (no optimizations), invalidate cache
            if (context.TotalOptimizations == 0 && _reportCache.Count > 0)
            {
                _reportCache.Clear();
            }
        }

        /// <summary>
        /// Generate an optimization report for a specific file from the context.
        /// </summary>
        private static CEOptimizationReport GenerateReportForFile(string filePath)
        {
            var context = GetOptimizationContext();
            if (context == null)
                return null;

            var entries = context.GetEntriesForFile(filePath).ToList();
            if (entries.Count == 0)
                return null;

            var report = new CEOptimizationReport
            {
                SourceFilePath = filePath
            };

            // Aggregate optimization counts by type. Entries recorded by a transform with no counter are
            // counted by GetSummaryStats but do not feed this per-behaviour report -- that is every
            // correctness-critical generator (sync glue, RPC, quantize, persistence, async), which the
            // report has no honest field for and which is not an optimization to begin with.
            foreach (var entry in entries)
            {
                if (_reportCounters.TryGetValue(entry.OptimizerId, out Action<CEOptimizationReport> counter))
                    counter(report);
            }

            // Calculate instruction reduction estimate
            // This is a rough estimate based on optimization types. The constant-folding and dead-code
            // terms stay at zero: both passes were retired to the binary level and record nothing here.
            int totalOpts = report.ConstantsFolded + report.LoopsUnrolled * 5 +
                            report.MethodsInlined * 3 + report.DeadCodeEliminated * 2;
            
            if (totalOpts > 0)
            {
                // Estimate ~2-10% reduction per optimization, capped at 50%
                report.InstructionReduction = Math.Min(50, totalOpts * 2);
            }

            return report;
        }

        /// <summary>
        /// Gets the optimization context from CEOptimizerRunner.
        /// </summary>
        private static OptimizationContext GetOptimizationContext()
        {
            try
            {
                return CEOptimizerRunner.GetContext();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Normalize file path for consistent caching.
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Convert to forward slashes and lowercase for consistency
            return path.Replace('\\', '/');
        }

        /// <summary>
        /// Gets all cached reports for debugging/display.
        /// </summary>
        public static IReadOnlyDictionary<string, CEOptimizationReport> GetAllCachedReports()
        {
            return _reportCache;
        }

        /// <summary>
        /// Gets summary statistics for all optimizations.
        /// </summary>
        public static (int totalFiles, int totalOptimizations, Dictionary<string, int> byType) GetSummaryStats()
        {
            var context = GetOptimizationContext();
            if (context == null)
                return (0, 0, new Dictionary<string, int>());

            var entries = context.GetEntries();
            var files = new HashSet<string>();
            var byType = new Dictionary<string, int>();

            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.FilePath))
                    files.Add(entry.FilePath);

                if (byType.TryGetValue(entry.OptimizerId, out int count))
                    byType[entry.OptimizerId] = count + 1;
                else
                    byType[entry.OptimizerId] = 1;
            }

            return (files.Count, context.TotalOptimizations, byType);
        }
    }
}

