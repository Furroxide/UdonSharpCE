using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.NetworkSim
{
    /// <summary>
    /// Editor window for configuring and monitoring network simulation.
    /// </summary>
    public class NetworkSimulatorWindow : EditorWindow
    {
        #region Menu Item

        [MenuItem("Udon CE/Dev Tools/Network Simulator", false, 1403)]
        public static void ShowWindow()
        {
            var window = GetWindow<NetworkSimulatorWindow>();
            window.titleContent = new GUIContent("CE Network Simulator");
            window.minSize = new Vector2(450, 550);
            window.Show();
        }

        #endregion

        #region State

        private NetworkSimulator _simulator;
        private int _selectedProfileIndex;
        private Vector2 _scrollPos;
        private Vector2 _eventsScrollPos;
        private bool _showAdvanced = false;
        private bool _showEvents = true;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _statusStyle;
        private bool _stylesInitialized;

        // Update tracking
        private double _lastUpdateTime;
        private const double UPDATE_INTERVAL = 0.5;

        #endregion

        #region Unity Callbacks

        private void OnEnable()
        {
            _simulator = NetworkSimulator.Instance;
            _simulator.OnConditionsChanged += OnConditionsChanged;
            _stylesInitialized = false;

            // Find current profile index
            UpdateProfileIndex();
        }

        private void OnDisable()
        {
            if (_simulator != null)
            {
                _simulator.OnConditionsChanged -= OnConditionsChanged;
            }
        }

        private void OnGUI()
        {
            InitializeStyles();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawHeader();

            EditorGUILayout.Space(10);

            DrawProfileSelector();

            EditorGUILayout.Space(10);

            DrawConditionsEditor();

            EditorGUILayout.Space(10);

            DrawStatistics();

            EditorGUILayout.Space(10);

            DrawEventLog();

            EditorGUILayout.Space(10);

            DrawActions();

            EditorGUILayout.EndScrollView();

            // Auto-repaint when active
            if (_simulator.IsActive && EditorApplication.timeSinceStartup - _lastUpdateTime > UPDATE_INTERVAL)
            {
                _lastUpdateTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        #endregion

        #region Drawing

        private void InitializeStyles()
        {
            if (_stylesInitialized)
                return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14
            };

            _statusStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter
            };

            _stylesInitialized = true;
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField("Network Simulator", _headerStyle);

            GUILayout.FlexibleSpace();

            // Status indicator
            bool isActive = _simulator.IsActive;
            Color statusColor = isActive ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.6f, 0.6f, 0.6f);
            string statusText = isActive ? "ACTIVE" : "INACTIVE";

            GUI.color = statusColor;
            GUILayout.Box(statusText, _statusStyle, GUILayout.Width(70), GUILayout.Height(20));
            GUI.color = Color.white;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Simulate network conditions to test your world's networking behaviour under various scenarios.",
                MessageType.Info);
        }

        private void DrawProfileSelector()
        {
            EditorGUILayout.LabelField("Network Profile", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Profile dropdown
            string[] profileNames = NetworkProfiles.GetProfileNames();
            EditorGUI.BeginChangeCheck();
            _selectedProfileIndex = EditorGUILayout.Popup("Profile", _selectedProfileIndex, profileNames);
            if (EditorGUI.EndChangeCheck())
            {
                if (_selectedProfileIndex < profileNames.Length - 1) // Not "Custom"
                {
                    _simulator.ApplyProfile(_selectedProfileIndex);
                }
            }

            // Quick profile buttons
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("None", EditorStyles.miniButtonLeft))
            {
                _simulator.ApplyProfile(0);
                _selectedProfileIndex = 0;
            }

            if (GUILayout.Button("WiFi", EditorStyles.miniButtonMid))
            {
                _simulator.ApplyProfile(1);
                _selectedProfileIndex = 1;
            }

            if (GUILayout.Button("4G", EditorStyles.miniButtonMid))
            {
                _simulator.ApplyProfile(3);
                _selectedProfileIndex = 3;
            }

            if (GUILayout.Button("Terrible", EditorStyles.miniButtonRight))
            {
                _simulator.ApplyProfile(6);
                _selectedProfileIndex = 6;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawConditionsEditor()
        {
            EditorGUILayout.LabelField("Conditions", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var conditions = _simulator.Conditions;

            // Enable toggle
            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUILayout.Toggle("Simulation Enabled", conditions.Enabled);
            if (EditorGUI.EndChangeCheck())
            {
                _simulator.SetEnabled(enabled);
            }

            EditorGUILayout.Space(5);

            // Latency
            EditorGUILayout.LabelField("Latency", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Min (ms)", GUILayout.Width(60));
            EditorGUI.BeginChangeCheck();
            float latencyMin = EditorGUILayout.Slider(conditions.LatencyMin, 0f, 2000f);
            if (EditorGUI.EndChangeCheck())
            {
                conditions.LatencyMin = latencyMin;
                _selectedProfileIndex = 7; // Custom
                _simulator.SetConditions(conditions);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Max (ms)", GUILayout.Width(60));
            EditorGUI.BeginChangeCheck();
            float latencyMax = EditorGUILayout.Slider(conditions.LatencyMax, 0f, 2000f);
            if (EditorGUI.EndChangeCheck())
            {
                conditions.LatencyMax = Mathf.Max(latencyMax, conditions.LatencyMin);
                _selectedProfileIndex = 7; // Custom
                _simulator.SetConditions(conditions);
            }
            EditorGUILayout.EndHorizontal();

            // Visualize latency range
            DrawLatencyBar(conditions.LatencyMin, conditions.LatencyMax);

            EditorGUILayout.Space(5);

            // Jitter
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Jitter (ms)", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            float jitter = EditorGUILayout.Slider(conditions.Jitter, 0f, 500f);
            if (EditorGUI.EndChangeCheck())
            {
                conditions.Jitter = jitter;
                _selectedProfileIndex = 7;
                _simulator.SetConditions(conditions);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Packet loss
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Packet Loss (%)", GUILayout.Width(100));
            EditorGUI.BeginChangeCheck();
            float packetLoss = EditorGUILayout.Slider(conditions.PacketLossPercent, 0f, 50f);
            if (EditorGUI.EndChangeCheck())
            {
                conditions.PacketLossPercent = packetLoss;
                _selectedProfileIndex = 7;
                _simulator.SetConditions(conditions);
            }
            EditorGUILayout.EndHorizontal();

            // Advanced settings
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced Settings", true);

            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;

                // Bandwidth limit
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Bandwidth (KB/s)", GUILayout.Width(110));
                EditorGUI.BeginChangeCheck();
                float bandwidth = EditorGUILayout.Slider(conditions.BandwidthLimitKBps, 0f, 5000f);
                if (EditorGUI.EndChangeCheck())
                {
                    conditions.BandwidthLimitKBps = bandwidth;
                    _selectedProfileIndex = 7;
                    _simulator.SetConditions(conditions);
                }
                EditorGUILayout.EndHorizontal();

                if (conditions.BandwidthLimitKBps > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"Effective bandwidth: ~{_simulator.EstimateEffectiveBandwidth():F1} KB/s",
                        MessageType.None);
                }

                // Out of order
                EditorGUI.BeginChangeCheck();
                bool outOfOrder = EditorGUILayout.Toggle("Simulate Out-of-Order", conditions.SimulateOutOfOrder);
                if (EditorGUI.EndChangeCheck())
                {
                    conditions.SimulateOutOfOrder = outOfOrder;
                    _selectedProfileIndex = 7;
                    _simulator.SetConditions(conditions);
                }

                // Duplicate
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Duplicate (%)", GUILayout.Width(100));
                EditorGUI.BeginChangeCheck();
                float duplicate = EditorGUILayout.Slider(conditions.DuplicatePercent, 0f, 20f);
                if (EditorGUI.EndChangeCheck())
                {
                    conditions.DuplicatePercent = duplicate;
                    _selectedProfileIndex = 7;
                    _simulator.SetConditions(conditions);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLatencyBar(float min, float max)
        {
            Rect rect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
            rect.x += 60;
            rect.width -= 60;

            // Background
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));

            // Scale: 0-2000ms
            float maxScale = 2000f;
            float minRatio = min / maxScale;
            float maxRatio = max / maxScale;

            // Range bar
            Rect rangeRect = rect;
            rangeRect.x = rect.x + rect.width * minRatio;
            rangeRect.width = rect.width * (maxRatio - minRatio);

            Color rangeColor = max < 100 ? new Color(0.2f, 0.7f, 0.2f) :
                               max < 500 ? new Color(0.9f, 0.7f, 0.1f) :
                               new Color(0.9f, 0.2f, 0.2f);

            EditorGUI.DrawRect(rangeRect, rangeColor);

            // Labels
            GUI.Label(new Rect(rect.x, rect.y, 40, 20), "0ms", EditorStyles.miniLabel);
            GUI.Label(new Rect(rect.xMax - 45, rect.y, 45, 20), "2000ms", EditorStyles.miniLabel);
        }

        private void DrawStatistics()
        {
            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var stats = _simulator.Stats;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Total Packets:", GUILayout.Width(120));
            EditorGUILayout.LabelField(stats.TotalPackets.ToString());
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Dropped:", GUILayout.Width(120));
            EditorGUILayout.LabelField($"{stats.DroppedPackets} ({stats.ActualLossRate:F1}%)");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Avg Latency:", GUILayout.Width(120));
            EditorGUILayout.LabelField($"{stats.AverageLatencyMs:F1} ms");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Total Data:", GUILayout.Width(120));
            EditorGUILayout.LabelField(FormatBytes(stats.TotalBytes));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Duration:", GUILayout.Width(120));
            EditorGUILayout.LabelField(stats.Duration.ToString(@"mm\:ss"));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawEventLog()
        {
            _showEvents = EditorGUILayout.Foldout(_showEvents, "Event Log", true);

            if (!_showEvents)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(150));

            _eventsScrollPos = EditorGUILayout.BeginScrollView(_eventsScrollPos);

            var events = _simulator.GetRecentEvents();

            if (events.Count == 0)
            {
                EditorGUILayout.LabelField("No events recorded yet.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                // Show events in reverse order (newest first)
                for (int i = events.Count - 1; i >= 0; i--)
                {
                    var evt = events[i];
                    DrawEventRow(evt);
                }
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawEventRow(PacketEvent evt)
        {
            EditorGUILayout.BeginHorizontal();

            // Time
            EditorGUILayout.LabelField(evt.Timestamp.ToString("HH:mm:ss.fff"), GUILayout.Width(90));

            // Event type with color
            Color typeColor = evt.EventType switch
            {
                PacketEventType.Delivered => new Color(0.2f, 0.7f, 0.2f),
                PacketEventType.Dropped => new Color(0.9f, 0.2f, 0.2f),
                PacketEventType.Duplicated => new Color(0.9f, 0.7f, 0.1f),
                PacketEventType.OutOfOrder => new Color(0.6f, 0.4f, 0.8f),
                _ => Color.white
            };

            GUI.color = typeColor;
            EditorGUILayout.LabelField(evt.EventType.ToString(), GUILayout.Width(80));
            GUI.color = Color.white;

            // Name
            EditorGUILayout.LabelField(evt.PacketName ?? "Unknown", GUILayout.Width(100));

            // Size
            EditorGUILayout.LabelField($"{evt.SizeBytes}B", GUILayout.Width(50));

            // Latency
            if (evt.EventType == PacketEventType.Delivered || evt.EventType == PacketEventType.Duplicated)
            {
                EditorGUILayout.LabelField($"+{evt.LatencyMs:F0}ms", GUILayout.Width(60));
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Reset Stats"))
            {
                _simulator.ResetStats();
            }

            if (GUILayout.Button("Test Packet"))
            {
                // Simulate a test packet
                var result = _simulator.SimulatePacket("TestPacket", 256);
                string resultStr = result.Dropped ? "DROPPED" :
                    result.Duplicated ? $"DUPLICATED (+{result.DelayMs:F0}ms)" :
                    $"DELIVERED (+{result.DelayMs:F0}ms)";
                Debug.Log($"[NetworkSimulator] Test packet: {resultStr}");
            }

            if (GUILayout.Button("Open Late-Join Simulator"))
            {
                LateJoinSimulatorWindow.ShowWindow();
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Helpers

        private void OnConditionsChanged(NetworkConditions conditions)
        {
            UpdateProfileIndex();
            Repaint();
        }

        private void UpdateProfileIndex()
        {
            var profiles = NetworkProfiles.GetAllProfiles();
            var conditions = _simulator.Conditions;

            for (int i = 0; i < profiles.Length; i++)
            {
                var profile = profiles[i];
                if (Mathf.Approximately(profile.LatencyMin, conditions.LatencyMin) &&
                    Mathf.Approximately(profile.LatencyMax, conditions.LatencyMax) &&
                    Mathf.Approximately(profile.PacketLossPercent, conditions.PacketLossPercent))
                {
                    _selectedProfileIndex = i;
                    return;
                }
            }

            _selectedProfileIndex = 7; // Custom
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes / (1024f * 1024f):F1} MB";
        }

        #endregion
    }
}

