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

            // Aggregate optimization counts by type
            foreach (var entry in entries)
            {
                switch (entry.OptimizerId)
                {
                    case "ConstantFolding":
                        report.ConstantsFolded++;
                        break;
                    case "SmallLoopUnrolling":
                        report.LoopsUnrolled++;
                        break;
                    case "TinyMethodInlining":
                        report.MethodsInlined++;
                        break;
                    case "StringInterning":
                        report.StringsInterned++;
                        break;
                    case "DeadCodeElimination":
                        report.DeadCodeEliminated++;
                        break;
                    // Sync optimizations are stubbed for now
                    case "SyncPacking":
                        report.SyncPackingApplied = true;
                        break;
                    case "DeltaSync":
                        report.DeltaSyncApplied = true;
                        report.DeltaSyncFields++;
                        break;
                }
            }

            // Calculate instruction reduction estimate
            // This is a rough estimate based on optimization types
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

