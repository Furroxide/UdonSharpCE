using System;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// Menu items for CE Inspector quick access.
    /// </summary>
    /// <remarks>
    /// ═══════════════════════════════════════════════════════════════════════
    /// UDON CE MENU ORGANIZATION GUIDELINES
    /// ═══════════════════════════════════════════════════════════════════════
    /// 
    /// All toolbar tools are unified under "Udon CE/" top-level menu.
    /// 
    /// RULE: Sections with 2 or fewer items should be FLATTENED to the root level.
    ///       Only create submenus when a section has 3+ items.
    /// 
    /// Current structure (priority ranges):
    ///   - Root level items (1101-1199): Refresh All Assets, Force Recompile, Optimization Report, etc.
    ///   - Inspector/         (1201-1299): 4+ items - Toggle settings, Preferences, Debug submenu
    ///   - Dev Tools/         (1401-1499): 4 items - Bandwidth, World Validator, Network, Late-Join
    ///   - Graph Bridge/      (1501-1599): 5 items - Browser, Validate, Generate nodes/wrappers/docs
    ///   - Debug/             (1901-1999): 3 items - Class Exposure Tree, Node Grabber, Parse Logs
    /// 
    /// When adding new items:
    ///   - If adding to a flattened section, check if it now has 3+ items → create submenu
    ///   - If removing from a submenu, check if it now has ≤2 items → flatten to root
    ///   - Add "Menu Guideline:" comment above MenuItem attributes for flattened items
    /// ═══════════════════════════════════════════════════════════════════════
    /// </remarks>
    public static class CEInspectorMenu
    {
        // ═══════════════════════════════════════════════════════════════
        // INSPECTOR MENU ITEMS (3+ items → keep submenu)
        // ═══════════════════════════════════════════════════════════════

        [MenuItem("Udon CE/Inspector/Toggle Optimization Info", false, 1201)]
        public static void ToggleOptimizationInfo()
        {
            CEInspectorBootstrap.ShowOptimizationInfo = !CEInspectorBootstrap.ShowOptimizationInfo;
            CEInspectorBootstrap.RefreshAllInspectors();

            Debug.Log($"[CE Inspector] Show Optimization Info: {(CEInspectorBootstrap.ShowOptimizationInfo ? "Enabled" : "Disabled")}");
        }

        [MenuItem("Udon CE/Inspector/Toggle Property Grouping", false, 1202)]
        public static void TogglePropertyGrouping()
        {
            CEInspectorBootstrap.AutoGroupProperties = !CEInspectorBootstrap.AutoGroupProperties;
            CEInspectorBootstrap.RefreshAllInspectors();

            Debug.Log($"[CE Inspector] Auto Property Grouping: {(CEInspectorBootstrap.AutoGroupProperties ? "Enabled" : "Disabled")}");
        }

        [MenuItem("Udon CE/Inspector/Refresh All", false, 1203)]
        public static void RefreshAllInspectors()
        {
            CEInspectorBootstrap.RefreshAllInspectors();
            Debug.Log("[CE Inspector] Refreshed all inspectors");
        }

        [MenuItem("Udon CE/Inspector/Preferences", false, 1204)]
        public static void OpenPreferences()
        {
            SettingsService.OpenUserPreferences("Preferences/UdonSharp CE/Inspector");
        }

        // ═══════════════════════════════════════════════════════════════
        // OPTIMIZATION MENU ITEMS
        // ═══════════════════════════════════════════════════════════════

        // Menu Guideline: Keep at root level unless Optimization section grows to 3+ items
        [MenuItem("Udon CE/Optimization Report", false, 1301)]
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
        [MenuItem("Udon CE/Inspector/Debug/Clear Style Cache", false, 1251)]
        public static void ClearStyleCache()
        {
            CEStyleCache.ClearCache();
            Debug.Log("[CE Inspector] Style cache cleared");
        }

        [MenuItem("Udon CE/Inspector/Debug/Clear Optimization Cache", false, 1252)]
        public static void ClearOptimizationCache()
        {
            CEOptimizationRegistry.ClearCache();
            Debug.Log("[CE Inspector] Optimization cache cleared");
        }

        [MenuItem("Udon CE/Inspector/Debug/Log Cached Reports", false, 1253)]
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

        [MenuItem("Udon CE/Inspector/Debug/Reset Preferences", false, 1254)]
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

