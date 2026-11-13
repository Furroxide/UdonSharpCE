using System;
using System.Collections.Generic;
using System.Linq;
using UdonSharp.CE.Editor.DevTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools
{
    /// <summary>
    /// Editor window for analyzing network bandwidth usage of UdonSharpBehaviours in a scene.
    /// Provides visualization of sync payload sizes and optimization recommendations.
    /// </summary>
    public class BandwidthAnalyzerWindow : EditorWindow
    {
        #region Menu Item

        // Menu Guideline: Dev Tools has 4 items → keep as submenu
        [MenuItem("Udon CE/Dev Tools/Bandwidth Analyzer", false, 1401)]
        public static void ShowWindow()
        {
            var window = GetWindow<BandwidthAnalyzerWindow>();
            window.titleContent = new GUIContent("CE Bandwidth Analyzer");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        #endregion

        #region State

        private WorldAnalysisResult _result;
        private Vector2 _scrollPos;
        private Dictionary<Type, bool> _foldouts = new Dictionary<Type, bool>();
        private string _searchFilter = "";
        private bool _showOnlyViolations = false;
        private bool _showRecommendations = true;

        // Cached styles
        private GUIStyle _headerStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _warningStyle;
        private GUIStyle _infoStyle;
        private bool _stylesInitialized;

        #endregion

        #region Unity Callbacks

        private void OnEnable()
        {
            _stylesInitialized = false;
        }

        private void OnGUI()
        {
            InitializeStyles();

            DrawToolbar();

            if (_result == null)
            {
                DrawEmptyState();
                return;
            }

            DrawSummary();

            EditorGUILayout.Space(5);

            DrawViolations();

            EditorGUILayout.Space(5);

            DrawBehaviourList();
        }

        #endregion

        #region Drawing Methods

        private void InitializeStyles()
        {
            if (_stylesInitialized)
                return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14
            };

            _errorStyle = new GUIStyle(EditorStyles.helpBox);
            _warningStyle = new GUIStyle(EditorStyles.helpBox);
            _infoStyle = new GUIStyle(EditorStyles.helpBox);

            _stylesInitialized = true;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Analyze Scene", EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                AnalyzeScene();
            }

            GUILayout.Space(10);

            // Search filter
            EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(150));

            GUILayout.Space(10);

            _showOnlyViolations = GUILayout.Toggle(_showOnlyViolations, "Only Violations", EditorStyles.toolbarButton);
            _showRecommendations = GUILayout.Toggle(_showRecommendations, "Show Tips", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();

            // Total bandwidth display
            if (_result != null)
            {
                float ratio = _result.TotalEstimatedBandwidthKBps / WorldAnalyzer.NETWORK_BUDGET_KBPS;
                Color statusColor = ratio < 0.7f ? new Color(0.2f, 0.8f, 0.2f) :
                                    ratio < 1.0f ? new Color(0.9f, 0.7f, 0.1f) :
                                    new Color(0.9f, 0.2f, 0.2f);

                GUI.color = statusColor;
                EditorGUILayout.LabelField(
                    $"Total: {FloorMark(HasLowerBound(_result))}{_result.TotalEstimatedBandwidthKBps:F1} / {WorldAnalyzer.NETWORK_BUDGET_KBPS:F0} KB/s",
                    EditorStyles.toolbarButton,
                    GUILayout.Width(160));
                GUI.color = Color.white;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEmptyState()
        {
            EditorGUILayout.Space(50);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(400));
            EditorGUILayout.HelpBox(
                "Click 'Analyze Scene' to calculate bandwidth usage for all UdonSharpBehaviours in the current scene.\n\n" +
                "This tool will show:\n" +
                "• Per-behaviour sync payload sizes\n" +
                "• Continuous sync limit violations\n" +
                "• Total bandwidth budget usage\n" +
                "• Optimization recommendations",
                MessageType.Info);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSummary()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Title
            EditorGUILayout.LabelField("World Bandwidth Summary", _headerStyle);

            EditorGUILayout.Space(5);

            // Bandwidth bar
            bool worldIsFloor = HasLowerBound(_result);

            DrawBandwidthBar("Total Bandwidth", _result.TotalEstimatedBandwidthKBps, WorldAnalyzer.NETWORK_BUDGET_KBPS, worldIsFloor);

            EditorGUILayout.Space(5);

            // Stats
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Synced Behaviours: {_result.TotalSyncedBehaviours}", GUILayout.Width(150));
            EditorGUILayout.LabelField($"Synced Fields: {_result.TotalSyncedFields}", GUILayout.Width(150));
            EditorGUILayout.LabelField($"Max Payload: {FloorMark(worldIsFloor)}{FieldSizeCalculator.FormatBytes(_result.TotalMaxBytes)}", GUILayout.Width(160));

            int errorCount = _result.Violations.Count(v => v.Severity == AnalysisSeverity.Error);
            int warningCount = _result.Violations.Count(v => v.Severity == AnalysisSeverity.Warning);

            if (errorCount > 0)
            {
                GUI.color = new Color(0.9f, 0.2f, 0.2f);
                EditorGUILayout.LabelField($"Errors: {errorCount}", GUILayout.Width(80));
            }
            if (warningCount > 0)
            {
                GUI.color = new Color(0.9f, 0.7f, 0.1f);
                EditorGUILayout.LabelField($"Warnings: {warningCount}", GUILayout.Width(100));
            }
            GUI.color = Color.white;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawViolations()
        {
            if (_result.Violations.Count == 0)
                return;

            EditorGUILayout.LabelField("Issues", _headerStyle);

            foreach (var violation in _result.Violations)
            {
                MessageType msgType = violation.Severity == AnalysisSeverity.Error ? MessageType.Error :
                                      violation.Severity == AnalysisSeverity.Warning ? MessageType.Warning :
                                      MessageType.Info;

                string message = violation.Message;
                if (!string.IsNullOrEmpty(violation.Recommendation))
                {
                    message += "\n" + violation.Recommendation;
                }

                EditorGUILayout.HelpBox(message, msgType);
            }
        }

        private void DrawBehaviourList()
        {
            EditorGUILayout.LabelField("By Behaviour", _headerStyle);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // Sort by bandwidth (highest first)
            var sorted = _result.BehaviourResults
                .OrderByDescending(b => b.TotalBandwidthKBps)
                .ToList();

            foreach (var behaviour in sorted)
            {
                // Apply search filter
                if (!string.IsNullOrEmpty(_searchFilter))
                {
                    if (!behaviour.BehaviourType.Name.ToLowerInvariant().Contains(_searchFilter.ToLowerInvariant()))
                        continue;
                }

                // Apply violations filter
                if (_showOnlyViolations && behaviour.Violations.Count == 0)
                    continue;

                DrawBehaviourSection(behaviour);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBehaviourSection(BehaviourAnalysisResult behaviour)
        {
            var type = behaviour.BehaviourType;
            if (!_foldouts.ContainsKey(type))
                _foldouts[type] = false;

            float totalBandwidth = behaviour.TotalBandwidthKBps;
            bool isFloor = HasLowerBound(behaviour);
            string header = $"{type.Name} (×{behaviour.InstanceCount}) — {FloorMark(isFloor)}{totalBandwidth:F2} KB/s";

            // Color-code by relative contribution
            float ratio = totalBandwidth / _result.TotalEstimatedBandwidthKBps;
            if (ratio > 0.5f)
                GUI.color = new Color(0.9f, 0.3f, 0.3f);
            else if (ratio > 0.2f)
                GUI.color = new Color(0.9f, 0.7f, 0.1f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _foldouts[type] = EditorGUILayout.Foldout(_foldouts[type], header, true);
            GUI.color = Color.white;

            if (_foldouts[type])
            {
                EditorGUI.indentLevel++;

                // Basic info. The source note matters: a behaviour with no [UdonBehaviourSyncMode] is
                // Continuous, not "None" as this window used to claim, but the author never wrote that down
                // and so has no idea this behaviour is on the 200-byte continuous budget at all.
                string syncModeSource = BehaviourAnalyzer.DescribeSyncModeSource(behaviour.BehaviourType);
                EditorGUILayout.LabelField(syncModeSource == null
                    ? $"Sync Mode: {behaviour.SyncMode}"
                    : $"Sync Mode: {behaviour.SyncMode} ({syncModeSource})");
                EditorGUILayout.LabelField(isFloor
                    ? $"Payload Size: >={behaviour.MaxTotalBytes} bytes (a field's length is not known ahead of time)"
                    : $"Payload Size: {behaviour.MinTotalBytes}–{behaviour.MaxTotalBytes} bytes");

                if (behaviour.ExceedsContinuousLimit)
                {
                    EditorGUILayout.HelpBox(
                        $"Exceeds {BehaviourAnalyzer.CONTINUOUS_SYNC_LIMIT}-byte continuous sync limit!",
                        MessageType.Error);
                }

                EditorGUILayout.Space(5);

                // Fields
                EditorGUILayout.LabelField("Synced Fields:", EditorStyles.miniBoldLabel);
                foreach (var field in behaviour.Fields.OrderByDescending(f => f.Size.MaxBytes))
                {
                    EditorGUILayout.BeginHorizontal();

                    EditorGUILayout.LabelField($"  {field.FieldName}", GUILayout.Width(150));
                    EditorGUILayout.LabelField(field.Size.Description, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"{FloorMark(field.IsLowerBound)}{field.Size.MaxBytes} B", GUILayout.Width(70));

                    EditorGUILayout.EndHorizontal();

                    if (!string.IsNullOrEmpty(field.Notes))
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.LabelField(field.Notes, EditorStyles.miniLabel);
                        EditorGUI.indentLevel--;
                    }
                }

                // Recommendations
                if (_showRecommendations && behaviour.Recommendations.Count > 0)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField("Recommendations:", EditorStyles.miniBoldLabel);

                    foreach (var rec in behaviour.Recommendations)
                    {
                        string fieldName = rec.Field?.Name ?? "Behaviour";
                        EditorGUILayout.HelpBox(
                            $"{fieldName}: {rec.Suggestion}" +
                            (rec.EstimatedSavings > 0 ? $" (saves ~{rec.EstimatedSavings}B)" : ""),
                            MessageType.Info);
                    }
                }

                // Behaviour-level violations
                foreach (var violation in behaviour.Violations)
                {
                    MessageType msgType = violation.Severity == AnalysisSeverity.Error ? MessageType.Error : MessageType.Warning;
                    EditorGUILayout.HelpBox(violation.Message, msgType);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawBandwidthBar(string label, float current, float max, bool isFloor)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(label, GUILayout.Width(120));

            Rect rect = GUILayoutUtility.GetRect(200, 20, GUILayout.ExpandWidth(true));
            float ratio = Mathf.Clamp01(current / max);

            // Background
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));

            // Fill
            Rect fillRect = rect;
            fillRect.width *= ratio;
            Color fillColor = ratio < 0.7f ? new Color(0.2f, 0.7f, 0.2f) :
                              ratio < 0.9f ? new Color(0.9f, 0.7f, 0.1f) :
                              new Color(0.9f, 0.2f, 0.2f);
            EditorGUI.DrawRect(fillRect, fillColor);

            // Border
            Handles.BeginGUI();
            Handles.color = new Color(0.5f, 0.5f, 0.5f);
            Handles.DrawLine(new Vector3(rect.x, rect.y), new Vector3(rect.xMax, rect.y));
            Handles.DrawLine(new Vector3(rect.xMax, rect.y), new Vector3(rect.xMax, rect.yMax));
            Handles.DrawLine(new Vector3(rect.xMax, rect.yMax), new Vector3(rect.x, rect.yMax));
            Handles.DrawLine(new Vector3(rect.x, rect.yMax), new Vector3(rect.x, rect.y));
            Handles.EndGUI();

            // Text overlay
            string text = $"{FloorMark(isFloor)}{current:F1} / {max:F0} KB/s ({FloorMark(isFloor)}{ratio * 100:F0}%)";
            GUI.Label(rect, text, new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                normal = { textColor = Color.white }
            });

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Lower Bounds

        // Every size and rate in this window is the analyzer's worst-case figure. For a field whose length
        // is not known ahead of time -- an unbounded string, an array no instance or [MaxLength] gave a
        // length to -- that figure is only the part that could be counted, and the field really costs
        // that or more. It used to be printed as a plain number, indistinguishable from an exact one; it
        // is now marked ">=" the way SizeResult renders it, and so is every total that includes one.

        internal static string FloorMark(bool isFloor)
        {
            return isFloor ? ">=" : "";
        }

        internal static bool HasLowerBound(BehaviourAnalysisResult behaviour)
        {
            return behaviour != null && behaviour.Fields.Any(field => field.IsLowerBound);
        }

        internal static bool HasLowerBound(WorldAnalysisResult world)
        {
            return world != null && world.BehaviourResults.Any(HasLowerBound);
        }

        #endregion

        #region Analysis

        private void AnalyzeScene()
        {
            try
            {
                var analyzer = new WorldAnalyzer();
                _result = analyzer.AnalyzeScene();
                _foldouts.Clear();
                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CE.DevTools] Bandwidth analysis failed: {ex.Message}\n{ex.StackTrace}");
                _result = null;
            }
        }

        #endregion
    }
}

