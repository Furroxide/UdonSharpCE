using System;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// Menu items for CE Inspector quick access.
    /// </summary>
    public static class CEInspectorMenu
    {
        // ═══════════════════════════════════════════════════════════════
        // INSPECTOR MENU ITEMS
        // ═══════════════════════════════════════════════════════════════

        [MenuItem("Tools/UdonSharp CE/Inspector/Toggle Optimization Info")]
        public static void ToggleOptimizationInfo()
        {
            CEInspectorBootstrap.ShowOptimizationInfo = !CEInspectorBootstrap.ShowOptimizationInfo;
            CEInspectorBootstrap.RefreshAllInspectors();

            Debug.Log($"[CE Inspector] Show Optimization Info: {(CEInspectorBootstrap.ShowOptimizationInfo ? "Enabled" : "Disabled")}");
        }

        [MenuItem("Tools/UdonSharp CE/Inspector/Toggle Property Grouping")]
        public static void TogglePropertyGrouping()
        {
            CEInspectorBootstrap.AutoGroupProperties = !CEInspectorBootstrap.AutoGroupProperties;
            CEInspectorBootstrap.RefreshAllInspectors();

            Debug.Log($"[CE Inspector] Auto Property Grouping: {(CEInspectorBootstrap.AutoGroupProperties ? "Enabled" : "Disabled")}");
        }

        [MenuItem("Tools/UdonSharp CE/Inspector/Refresh All Inspectors")]
        public static void RefreshAllInspectors()
        {
            CEInspectorBootstrap.RefreshAllInspectors();
            Debug.Log("[CE Inspector] Refreshed all inspectors");
        }

        [MenuItem("Tools/UdonSharp CE/Inspector/Open Preferences")]
        public static void OpenPreferences()
        {
            SettingsService.OpenUserPreferences("Preferences/UdonSharp CE/Inspector");
        }

        // ═══════════════════════════════════════════════════════════════
        // OPTIMIZATION MENU ITEMS
        // ═══════════════════════════════════════════════════════════════

        [MenuItem("Tools/UdonSharp CE/Optimization Report")]
        public static void OpenOptimizationReport()
        {
            // Try to open the optimization report window
            var windowType = Type.GetType("UdonSharp.CE.Editor.Optimizers.OptimizationReportWindow, UdonSharp.CE.Editor");
            if (windowType != null)
            {
                EditorWindow.GetWindow(windowType, false, "CE Optimization Report");
            }
            else
            {
                // Fallback: show summary in console
                ShowOptimizationSummary();
            }
        }

        private static void ShowOptimizationSummary()
        {
            var (totalFiles, totalOpts, byType) = CEOptimizationRegistry.GetSummaryStats();

            if (totalOpts == 0)
            {
                Debug.Log("[CE Optimizer] No optimizations recorded. Compile your U# scripts to see optimization stats.");
                return;
            }

            var summary = $"[CE Optimizer] Summary:\n" +
                          $"  Files optimized: {totalFiles}\n" +
                          $"  Total optimizations: {totalOpts}\n";

            foreach (var (type, count) in byType)
            {
                summary += $"  - {type}: {count}\n";
            }

            Debug.Log(summary);
        }

        // ═══════════════════════════════════════════════════════════════
        // DEBUG MENU ITEMS
        // ═══════════════════════════════════════════════════════════════

#if UDONSHARP_DEBUG
        [MenuItem("Tools/UdonSharp CE/Inspector/Debug/Clear Style Cache")]
        public static void ClearStyleCache()
        {
            CEStyleCache.ClearCache();
            Debug.Log("[CE Inspector] Style cache cleared");
        }

        [MenuItem("Tools/UdonSharp CE/Inspector/Debug/Clear Optimization Cache")]
        public static void ClearOptimizationCache()
        {
            CEOptimizationRegistry.ClearCache();
            Debug.Log("[CE Inspector] Optimization cache cleared");
        }

        [MenuItem("Tools/UdonSharp CE/Inspector/Debug/Log Cached Reports")]
        public static void LogCachedReports()
        {
            var reports = CEOptimizationRegistry.GetAllCachedReports();
            
            if (reports.Count == 0)
            {
                Debug.Log("[CE Inspector] No cached optimization reports");
                return;
            }

            Debug.Log($"[CE Inspector] Cached optimization reports ({reports.Count}):");
            foreach (var (path, report) in reports)
            {
                Debug.Log($"  {path}: {report.TotalOptimizations} optimizations");
            }
        }

        [MenuItem("Tools/UdonSharp CE/Inspector/Debug/Reset Preferences")]
        public static void ResetPreferences()
        {
            CEInspectorBootstrap.ResetToDefaults();
        }
#endif

        // ═══════════════════════════════════════════════════════════════
        // CONTEXT MENU ITEMS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Add context menu item to UdonSharpBehaviour components.
        /// </summary>
        [MenuItem("CONTEXT/UdonSharpBehaviour/Show CE Optimization Info")]
        public static void ShowOptimizationInfoContext(MenuCommand command)
        {
            if (command.context == null) return;

            var report = CEOptimizationRegistry.GetReport(command.context);
            
            if (report == null || !report.AnyOptimizationsApplied)
            {
                Debug.Log($"[CE] No optimizations applied to {command.context.GetType().Name}");
                return;
            }

            Debug.Log($"[CE] Optimization Report for {command.context.GetType().Name}:\n" +
                      $"  Constants Folded: {report.ConstantsFolded}\n" +
                      $"  Loops Unrolled: {report.LoopsUnrolled}\n" +
                      $"  Methods Inlined: {report.MethodsInlined}\n" +
                      $"  Strings Interned: {report.StringsInterned}\n" +
                      $"  Dead Code Eliminated: {report.DeadCodeEliminated}\n" +
                      $"  Estimated Instruction Reduction: ~{report.InstructionReduction}%");
        }
    }
}

