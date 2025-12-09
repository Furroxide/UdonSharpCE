using UnityEditor;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// Shared rendering utilities for CE inspectors.
    /// Provides consistent UI elements across all CE inspector components.
    /// </summary>
    public static class CEInspectorRenderer
    {
        // ═══════════════════════════════════════════════════════════════
        // HEADER RENDERING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw the CE inspector header with icon, class name, and badges.
        /// </summary>
        /// <param name="className">The name of the UdonSharpBehaviour class.</param>
        /// <param name="badges">Array of badges to display.</param>
        public static void DrawHeader(string className, CEBadge[] badges)
        {
            EditorGUILayout.BeginHorizontal(CEStyleCache.HeaderStyle);
            {
                // CE diamond icon
                var iconRect = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18), GUILayout.Height(18));
                if (CEStyleCache.DiamondIcon != null)
                {
                    GUI.DrawTexture(iconRect, CEStyleCache.DiamondIcon, ScaleMode.ScaleToFit);
                }
                
                GUILayout.Space(4);

                // Class name
                GUILayout.Label(className, CEStyleCache.HeaderTextStyle);

                GUILayout.FlexibleSpace();

                // Badges
                if (badges != null)
                {
                    foreach (var badge in badges)
                    {
                        DrawBadge(badge);
                        GUILayout.Space(2);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draw a single badge.
        /// </summary>
        private static void DrawBadge(CEBadge badge)
        {
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = badge.Color;

            var content = string.IsNullOrEmpty(badge.Tooltip)
                ? new GUIContent(badge.Text)
                : new GUIContent(badge.Text, badge.Tooltip);

            GUILayout.Label(content, CEStyleCache.BadgeStyle);

            GUI.backgroundColor = prevBg;
        }

        // ═══════════════════════════════════════════════════════════════
        // STATUS BAR RENDERING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw the sync status bar showing sync mode, variable count, and bandwidth info.
        /// </summary>
        /// <param name="syncInfo">Sync information for the behaviour.</param>
        /// <param name="report">Optional optimization report for bandwidth savings.</param>
        public static void DrawStatusBar(SyncInfo syncInfo, CEOptimizationReport report)
        {
            if (syncInfo == null)
                return;

            // Choose background color based on sync mode
            Color bgColor;
            switch (syncInfo.Mode)
            {
                case Networking.SyncType.Manual:
                    bgColor = CEColors.SyncManualBg;
                    break;
                case Networking.SyncType.Continuous:
                    bgColor = CEColors.SyncContinuousBg;
                    break;
                default:
                    bgColor = CEColors.SyncNoneBg;
                    break;
            }

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;

            EditorGUILayout.BeginHorizontal(CEStyleCache.StatusBarStyle);
            {
                // Sync mode
                string syncModeName = syncInfo.Mode == Networking.SyncType.None 
                    ? "None" 
                    : syncInfo.Mode.ToString();
                GUILayout.Label($"Sync: {syncModeName}", CEStyleCache.StatusTextStyle);

                if (syncInfo.SyncedFieldCount > 0)
                {
                    GUILayout.Label("│", CEStyleCache.StatusSeparatorStyle);

                    // Synced variable count with optimization info
                    if (syncInfo.HasSyncOptimizations)
                    {
                        GUILayout.Label(
                            $"{syncInfo.SyncedFieldCount} → {syncInfo.OptimizedFieldCount} vars (packed)",
                            CEStyleCache.StatusPositiveStyle
                        );
                    }
                    else
                    {
                        GUILayout.Label(
                            $"{syncInfo.SyncedFieldCount} synced var{(syncInfo.SyncedFieldCount != 1 ? "s" : "")}",
                            CEStyleCache.StatusTextStyle
                        );
                    }

                    // Bandwidth savings
                    int bandwidthReduction = report?.BandwidthReduction ?? syncInfo.BandwidthReductionPercent;
                    if (bandwidthReduction > 0)
                    {
                        GUILayout.Label("│", CEStyleCache.StatusSeparatorStyle);
                        GUILayout.Label(
                            $"-{bandwidthReduction}% bandwidth",
                            CEStyleCache.StatusPositiveStyle
                        );
                    }
                }

                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndHorizontal();

            GUI.backgroundColor = prevBg;
        }

        // ═══════════════════════════════════════════════════════════════
        // GROUP HEADER RENDERING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw a collapsible group header.
        /// </summary>
        /// <param name="name">Group name.</param>
        /// <param name="expanded">Current expanded state.</param>
        /// <param name="count">Number of items in group (-1 to hide count).</param>
        /// <returns>New expanded state.</returns>
        public static bool DrawGroupHeader(string name, bool expanded, int count)
        {
            EditorGUILayout.BeginHorizontal(CEStyleCache.GroupHeaderStyle);
            {
                expanded = EditorGUILayout.Foldout(expanded, name, true, CEStyleCache.FoldoutStyle);

                if (count >= 0)
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"({count})", CEStyleCache.GroupCountStyle);
                }
            }
            EditorGUILayout.EndHorizontal();

            return expanded;
        }

        /// <summary>
        /// Begin a group box visual container.
        /// </summary>
        public static void BeginGroupBox()
        {
            EditorGUILayout.BeginVertical(CEStyleCache.GroupBoxStyle);
        }

        /// <summary>
        /// End a group box visual container.
        /// </summary>
        public static void EndGroupBox()
        {
            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════
        // OPTIMIZATION PANEL RENDERING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw the optimization info panel showing applied CE optimizations.
        /// </summary>
        /// <param name="report">The optimization report to display.</param>
        public static void DrawOptimizationPanel(CEOptimizationReport report)
        {
            if (report == null || !report.AnyOptimizationsApplied)
                return;

            EditorGUILayout.BeginVertical(CEStyleCache.OptimizationPanelStyle);
            {
                EditorGUILayout.LabelField("CE Optimizations", CEStyleCache.OptimizationHeaderStyle);
                EditorGUILayout.Space(2);

                // Sync optimizations (currently stubbed)
                if (report.SyncPackingApplied)
                {
                    DrawOptimizationItem(
                        "Sync Packing",
                        $"{report.OriginalSyncVars} → {report.PackedSyncVars} variables"
                    );
                }

                if (report.DeltaSyncApplied)
                {
                    DrawOptimizationItem(
                        "Delta Sync",
                        $"{report.DeltaSyncFields} field{(report.DeltaSyncFields != 1 ? "s" : "")}"
                    );
                }

                // Execution optimizations
                if (report.ConstantsFolded > 0)
                {
                    DrawOptimizationItem(
                        "Constants Folded",
                        $"{report.ConstantsFolded} expression{(report.ConstantsFolded != 1 ? "s" : "")}"
                    );
                }

                if (report.LoopsUnrolled > 0)
                {
                    DrawOptimizationItem(
                        "Loops Unrolled",
                        $"{report.LoopsUnrolled} loop{(report.LoopsUnrolled != 1 ? "s" : "")}"
                    );
                }

                if (report.MethodsInlined > 0)
                {
                    DrawOptimizationItem(
                        "Methods Inlined",
                        $"{report.MethodsInlined} method{(report.MethodsInlined != 1 ? "s" : "")}"
                    );
                }

                if (report.StringsInterned > 0)
                {
                    DrawOptimizationItem(
                        "Strings Interned",
                        $"{report.StringsInterned} string{(report.StringsInterned != 1 ? "s" : "")}"
                    );
                }

                if (report.DeadCodeEliminated > 0)
                {
                    DrawOptimizationItem(
                        "Dead Code Removed",
                        $"{report.DeadCodeEliminated} block{(report.DeadCodeEliminated != 1 ? "s" : "")}"
                    );
                }

                // Summary
                if (report.InstructionReduction > 0)
                {
                    EditorGUILayout.Space(4);
                    var summaryStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleRight
                    };
                    summaryStyle.normal.textColor = CEColors.TextSecondary;
                    
                    EditorGUILayout.LabelField(
                        $"Est. ~{report.InstructionReduction}% instruction reduction",
                        summaryStyle
                    );
                }
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draw a single optimization item row.
        /// </summary>
        private static void DrawOptimizationItem(string name, string value)
        {
            EditorGUILayout.BeginHorizontal();
            {
                GUILayout.Label("✓", CEStyleCache.OptimizationCheckStyle, GUILayout.Width(16));
                GUILayout.Label(name, CEStyleCache.OptimizationNameStyle, GUILayout.Width(120));
                GUILayout.Label(value, CEStyleCache.OptimizationValueStyle);
            }
            EditorGUILayout.EndHorizontal();
        }

        // ═══════════════════════════════════════════════════════════════
        // UTILITY METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw a horizontal separator line.
        /// </summary>
        /// <param name="color">Line color (default: gray).</param>
        /// <param name="thickness">Line thickness in pixels.</param>
        /// <param name="padding">Vertical padding around line.</param>
        public static void DrawSeparator(Color? color = null, int thickness = 1, int padding = 4)
        {
            Color lineColor = color ?? new Color(0.5f, 0.5f, 0.5f, 0.5f);
            
            Rect rect = EditorGUILayout.GetControlRect(GUILayout.Height(padding + thickness));
            rect.height = thickness;
            rect.y += padding / 2f;
            rect.x -= 2;
            rect.width += 6;
            
            EditorGUI.DrawRect(rect, lineColor);
        }

        /// <summary>
        /// Draw a help box with CE styling.
        /// </summary>
        /// <param name="message">Message to display.</param>
        /// <param name="type">Message type (info, warning, error).</param>
        public static void DrawHelpBox(string message, MessageType type = MessageType.Info)
        {
            EditorGUILayout.HelpBox(message, type);
        }

        /// <summary>
        /// Draw a compact info label.
        /// </summary>
        /// <param name="label">Label text.</param>
        /// <param name="value">Value text.</param>
        public static void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
                
                var valueStyle = new GUIStyle(EditorStyles.label);
                valueStyle.normal.textColor = CEColors.TextSecondary;
                EditorGUILayout.LabelField(value, valueStyle);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}

