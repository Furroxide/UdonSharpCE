using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace UdonSharp.CE.Editor.DevTools.NetworkSim
{
    /// <summary>
    /// Simulates late-join scenarios for testing sync behaviour.
    /// Captures scene state, clears synced variables, and tests reconstruction.
    /// </summary>
    public class LateJoinSimulator
    {
        #region Singleton

        private static LateJoinSimulator _instance;

        /// <summary>
        /// Gets the singleton instance.
        /// </summary>
        public static LateJoinSimulator Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new LateJoinSimulator();
                }
                return _instance;
            }
        }

        #endregion

        #region State

        /// <summary>
        /// Captured state snapshots.
        /// </summary>
        public List<BehaviourSnapshot> CapturedSnapshots { get; private set; } = new List<BehaviourSnapshot>();

        /// <summary>
        /// Results from the last simulation.
        /// </summary>
        public LateJoinSimulationResult LastResult { get; private set; }

        /// <summary>
        /// Whether a simulation is in progress.
        /// </summary>
        public bool IsSimulating { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Captures the current state of all synced variables in the scene.
        /// </summary>
        public void CaptureCurrentState()
        {
            CapturedSnapshots.Clear();

            var behaviours = UnityEngine.Object.FindObjectsOfType<UdonSharpBehaviour>();

            foreach (var behaviour in behaviours)
            {
                var snapshot = CaptureBehaviourState(behaviour);
                if (snapshot != null && snapshot.SyncedFields.Count > 0)
                {
                    CapturedSnapshots.Add(snapshot);
                }
            }

            Debug.Log($"[LateJoinSimulator] Captured state of {CapturedSnapshots.Count} behaviours with {CapturedSnapshots.Sum(s => s.SyncedFields.Count)} synced fields");
        }

        /// <summary>
        /// Simulates a late-join by clearing all synced variables and triggering reconstruction.
        /// </summary>
        public LateJoinSimulationResult SimulateLateJoin(float delaySeconds = 0f)
        {
            if (CapturedSnapshots.Count == 0)
            {
                Debug.LogWarning("[LateJoinSimulator] No state captured. Call CaptureCurrentState first.");
                return null;
            }

            IsSimulating = true;

            var result = new LateJoinSimulationResult
            {
                StartTime = DateTime.Now,
                TotalBehaviours = CapturedSnapshots.Count,
                TotalSyncedFields = CapturedSnapshots.Sum(s => s.SyncedFields.Count)
            };

            // Clear synced variables
            ClearSyncedVariables();

            // Wait for delay (simulating network latency)
            if (delaySeconds > 0)
            {
                result.SimulatedDelay = delaySeconds;
                // In a real scenario, we'd use coroutines or async
                // For editor, we'll note the delay but proceed immediately
            }

            // Trigger late-join reconstruction
            TriggerLateJoinReconstruction();

            // Verify reconstruction
            result.ReconstructionResults = VerifyReconstruction();

            result.EndTime = DateTime.Now;
            result.SuccessRate = result.ReconstructionResults.Count > 0
                ? (float)result.ReconstructionResults.Count(r => r.Reconstructed) / result.ReconstructionResults.Count
                : 0f;

            LastResult = result;
            IsSimulating = false;

            Debug.Log($"[LateJoinSimulator] Simulation complete. Success rate: {result.SuccessRate * 100:F1}%");

            return result;
        }

        /// <summary>
        /// Restores the captured state to all behaviours.
        /// </summary>
        public void RestoreCapturedState()
        {
            if (CapturedSnapshots.Count == 0)
            {
                Debug.LogWarning("[LateJoinSimulator] No state captured to restore.");
                return;
            }

            int restoredCount = 0;

            foreach (var snapshot in CapturedSnapshots)
            {
                if (snapshot.Behaviour == null)
                    continue;

                foreach (var field in snapshot.SyncedFields)
                {
                    try
                    {
                        SetFieldValue(snapshot.Behaviour, field.FieldName, field.CapturedValue);
                        restoredCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[LateJoinSimulator] Failed to restore {field.FieldName}: {ex.Message}");
                    }
                }
            }

            Debug.Log($"[LateJoinSimulator] Restored {restoredCount} field values");
        }

        /// <summary>
        /// Analyzes the scene for [SyncOnJoin] attributes and other late-join related code.
        /// </summary>
        public LateJoinAnalysis AnalyzeScene()
        {
            var analysis = new LateJoinAnalysis();

            var behaviours = UnityEngine.Object.FindObjectsOfType<UdonSharpBehaviour>();

            foreach (var behaviour in behaviours)
            {
                var behaviourType = behaviour.GetType();
                var fields = behaviourType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                foreach (var field in fields)
                {
                    // Check for UdonSynced attribute
                    var syncAttr = field.GetCustomAttribute<UdonSyncedAttribute>();
                    if (syncAttr != null)
                    {
                        analysis.TotalSyncedFields++;

                        // Check for SyncOnJoin (CE attribute)
                        var syncOnJoinAttr = field.GetCustomAttributes()
                            .FirstOrDefault(a => a.GetType().Name == "SyncOnJoinAttribute");

                        if (syncOnJoinAttr != null)
                        {
                            analysis.SyncOnJoinFields++;
                        }
                    }
                }

                // Check for OnPlayerJoined handler
                var onPlayerJoined = behaviourType.GetMethod("OnPlayerJoined",
                    BindingFlags.Public | BindingFlags.Instance);
                if (onPlayerJoined != null)
                {
                    analysis.OnPlayerJoinedHandlers++;
                }

                // Check for OnDeserialization handler
                var onDeserialization = behaviourType.GetMethod("OnDeserialization",
                    BindingFlags.Public | BindingFlags.Instance);
                if (onDeserialization != null)
                {
                    analysis.OnDeserializationHandlers++;
                }
            }

            analysis.TotalBehaviours = behaviours.Length;

            return analysis;
        }

        #endregion

        #region Internal Methods

        private BehaviourSnapshot CaptureBehaviourState(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return null;

            var snapshot = new BehaviourSnapshot
            {
                Behaviour = behaviour,
                BehaviourType = behaviour.GetType(),
                GameObjectName = behaviour.gameObject.name
            };

            var fields = snapshot.BehaviourType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                var syncAttr = field.GetCustomAttribute<UdonSyncedAttribute>();
                if (syncAttr == null)
                    continue;

                try
                {
                    var value = field.GetValue(behaviour);
                    snapshot.SyncedFields.Add(new FieldSnapshot
                    {
                        FieldName = field.Name,
                        FieldType = field.FieldType,
                        CapturedValue = CloneValue(value),
                        SyncMode = syncAttr.NetworkSyncType.ToString()
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LateJoinSimulator] Failed to capture {field.Name}: {ex.Message}");
                }
            }

            return snapshot;
        }

        private void ClearSyncedVariables()
        {
            foreach (var snapshot in CapturedSnapshots)
            {
                if (snapshot.Behaviour == null)
                    continue;

                foreach (var field in snapshot.SyncedFields)
                {
                    try
                    {
                        var defaultValue = GetDefaultValue(field.FieldType);
                        SetFieldValue(snapshot.Behaviour, field.FieldName, defaultValue);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[LateJoinSimulator] Failed to clear {field.FieldName}: {ex.Message}");
                    }
                }
            }

            Debug.Log("[LateJoinSimulator] Cleared all synced variables");
        }

        private void TriggerLateJoinReconstruction()
        {
            // Call OnDeserialization on all behaviours
            foreach (var snapshot in CapturedSnapshots)
            {
                if (snapshot.Behaviour == null)
                    continue;

                try
                {
                    // Check for OnDeserialization method
                    var method = snapshot.BehaviourType.GetMethod("OnDeserialization",
                        BindingFlags.Public | BindingFlags.Instance);

                    if (method != null)
                    {
                        method.Invoke(snapshot.Behaviour, null);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LateJoinSimulator] OnDeserialization failed for {snapshot.GameObjectName}: {ex.Message}");
                }
            }

            Debug.Log("[LateJoinSimulator] Triggered late-join reconstruction");
        }

        private List<FieldReconstructionResult> VerifyReconstruction()
        {
            var results = new List<FieldReconstructionResult>();

            foreach (var snapshot in CapturedSnapshots)
            {
                if (snapshot.Behaviour == null)
                    continue;

                foreach (var field in snapshot.SyncedFields)
                {
                    var result = new FieldReconstructionResult
                    {
                        BehaviourName = snapshot.GameObjectName,
                        FieldName = field.FieldName,
                        OriginalValue = field.CapturedValue
                    };

                    try
                    {
                        var fieldInfo = snapshot.BehaviourType.GetField(field.FieldName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                        if (fieldInfo != null)
                        {
                            result.CurrentValue = fieldInfo.GetValue(snapshot.Behaviour);
                            result.Reconstructed = ValuesEqual(result.OriginalValue, result.CurrentValue);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                    }

                    results.Add(result);
                }
            }

            return results;
        }

        private object CloneValue(object value)
        {
            if (value == null)
                return null;

            // Handle arrays
            if (value is Array array)
            {
                var clone = Array.CreateInstance(array.GetType().GetElementType(), array.Length);
                Array.Copy(array, clone, array.Length);
                return clone;
            }

            // Value types are copied by value
            if (value.GetType().IsValueType)
                return value;

            // Strings are immutable
            if (value is string)
                return value;

            // For other reference types, just return the reference
            // (we can't deep clone arbitrary objects)
            return value;
        }

        private object GetDefaultValue(Type type)
        {
            if (type == null)
                return null;

            if (type.IsValueType)
                return Activator.CreateInstance(type);

            if (type.IsArray)
                return null;

            return null;
        }

        private void SetFieldValue(UdonSharpBehaviour behaviour, string fieldName, object value)
        {
            var field = behaviour.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                field.SetValue(behaviour, value);
            }
        }

        private bool ValuesEqual(object a, object b)
        {
            if (a == null && b == null)
                return true;

            if (a == null || b == null)
                return false;

            if (a is Array arrayA && b is Array arrayB)
            {
                if (arrayA.Length != arrayB.Length)
                    return false;

                for (int i = 0; i < arrayA.Length; i++)
                {
                    if (!ValuesEqual(arrayA.GetValue(i), arrayB.GetValue(i)))
                        return false;
                }

                return true;
            }

            return a.Equals(b);
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Snapshot of a behaviour's synced state.
    /// </summary>
    public class BehaviourSnapshot
    {
        public UdonSharpBehaviour Behaviour;
        public Type BehaviourType;
        public string GameObjectName;
        public List<FieldSnapshot> SyncedFields = new List<FieldSnapshot>();
    }

    /// <summary>
    /// Snapshot of a single synced field.
    /// </summary>
    public class FieldSnapshot
    {
        public string FieldName;
        public Type FieldType;
        public object CapturedValue;
        public string SyncMode;
    }

    /// <summary>
    /// Result of a late-join simulation.
    /// </summary>
    public class LateJoinSimulationResult
    {
        public DateTime StartTime;
        public DateTime EndTime;
        public int TotalBehaviours;
        public int TotalSyncedFields;
        public float SimulatedDelay;
        public float SuccessRate;
        public List<FieldReconstructionResult> ReconstructionResults = new List<FieldReconstructionResult>();

        public TimeSpan Duration => EndTime - StartTime;
    }

    /// <summary>
    /// Result of reconstructing a single field.
    /// </summary>
    public class FieldReconstructionResult
    {
        public string BehaviourName;
        public string FieldName;
        public object OriginalValue;
        public object CurrentValue;
        public bool Reconstructed;
        public string Error;
    }

    /// <summary>
    /// Analysis of late-join handling in the scene.
    /// </summary>
    public class LateJoinAnalysis
    {
        public int TotalBehaviours;
        public int TotalSyncedFields;
        public int SyncOnJoinFields;
        public int OnPlayerJoinedHandlers;
        public int OnDeserializationHandlers;

        public float SyncOnJoinCoverage => TotalSyncedFields > 0
            ? (float)SyncOnJoinFields / TotalSyncedFields
            : 0f;
    }

    #endregion

    /// <summary>
    /// Editor window for the late-join simulator.
    /// </summary>
    public class LateJoinSimulatorWindow : EditorWindow
    {
        #region Menu Item

        [MenuItem("CE Tools/Late-Join Simulator", priority = 104)]
        public static void ShowWindow()
        {
            var window = GetWindow<LateJoinSimulatorWindow>();
            window.titleContent = new GUIContent("CE Late-Join Simulator");
            window.minSize = new Vector2(500, 450);
            window.Show();
        }

        #endregion

        #region State

        private LateJoinSimulator _simulator;
        private Vector2 _scrollPos;
        private float _simulatedDelay = 0f;
        private bool _showResults = true;
        private bool _showAnalysis = true;

        private LateJoinAnalysis _lastAnalysis;

        // Styles
        private GUIStyle _headerStyle;
        private bool _stylesInitialized;

        #endregion

        #region Unity Callbacks

        private void OnEnable()
        {
            _simulator = LateJoinSimulator.Instance;
            _stylesInitialized = false;
        }

        private void OnGUI()
        {
            InitializeStyles();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawHeader();

            EditorGUILayout.Space(10);

            DrawAnalysis();

            EditorGUILayout.Space(10);

            DrawCaptureControls();

            EditorGUILayout.Space(10);

            DrawSimulationControls();

            EditorGUILayout.Space(10);

            DrawResults();

            EditorGUILayout.EndScrollView();
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

            _stylesInitialized = true;
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Late-Join Simulator", _headerStyle);

            EditorGUILayout.HelpBox(
                "Test how your world handles players joining mid-session.\n\n" +
                "1. Capture the current state of synced variables\n" +
                "2. Simulate a late-join (clears and reconstructs state)\n" +
                "3. Verify that reconstruction was successful",
                MessageType.Info);
        }

        private void DrawAnalysis()
        {
            _showAnalysis = EditorGUILayout.Foldout(_showAnalysis, "Scene Analysis", true);

            if (!_showAnalysis)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (GUILayout.Button("Analyze Scene", GUILayout.Height(25)))
            {
                _lastAnalysis = _simulator.AnalyzeScene();
            }

            if (_lastAnalysis != null)
            {
                EditorGUILayout.Space(5);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Total Behaviours:", GUILayout.Width(150));
                EditorGUILayout.LabelField(_lastAnalysis.TotalBehaviours.ToString());
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Synced Fields:", GUILayout.Width(150));
                EditorGUILayout.LabelField(_lastAnalysis.TotalSyncedFields.ToString());
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("[SyncOnJoin] Fields:", GUILayout.Width(150));
                EditorGUILayout.LabelField($"{_lastAnalysis.SyncOnJoinFields} ({_lastAnalysis.SyncOnJoinCoverage * 100:F0}%)");
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("OnPlayerJoined Handlers:", GUILayout.Width(150));
                EditorGUILayout.LabelField(_lastAnalysis.OnPlayerJoinedHandlers.ToString());
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("OnDeserialization Handlers:", GUILayout.Width(150));
                EditorGUILayout.LabelField(_lastAnalysis.OnDeserializationHandlers.ToString());
                EditorGUILayout.EndHorizontal();

                if (_lastAnalysis.SyncOnJoinCoverage < 1f && _lastAnalysis.TotalSyncedFields > 0)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox(
                        $"Only {_lastAnalysis.SyncOnJoinCoverage * 100:F0}% of synced fields use [SyncOnJoin]. " +
                        "Consider adding this attribute to fields that need to sync to late-joiners.",
                        MessageType.Warning);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawCaptureControls()
        {
            EditorGUILayout.LabelField("State Capture", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Capture Current State", GUILayout.Height(30)))
            {
                _simulator.CaptureCurrentState();
            }

            GUI.enabled = _simulator.CapturedSnapshots.Count > 0;
            if (GUILayout.Button("Restore Captured State", GUILayout.Height(30)))
            {
                _simulator.RestoreCapturedState();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // Show capture stats
            if (_simulator.CapturedSnapshots.Count > 0)
            {
                EditorGUILayout.Space(5);
                int totalFields = _simulator.CapturedSnapshots.Sum(s => s.SyncedFields.Count);
                EditorGUILayout.LabelField(
                    $"Captured: {_simulator.CapturedSnapshots.Count} behaviours, {totalFields} fields",
                    EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSimulationControls()
        {
            EditorGUILayout.LabelField("Simulation", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Delay setting
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Simulated Delay (s):", GUILayout.Width(130));
            _simulatedDelay = EditorGUILayout.Slider(_simulatedDelay, 0f, 5f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            GUI.enabled = _simulator.CapturedSnapshots.Count > 0 && !_simulator.IsSimulating;

            if (GUILayout.Button("Simulate Late-Join", GUILayout.Height(35)))
            {
                _simulator.SimulateLateJoin(_simulatedDelay);
            }

            GUI.enabled = true;

            if (_simulator.CapturedSnapshots.Count == 0)
            {
                EditorGUILayout.HelpBox("Capture state first before simulating.", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawResults()
        {
            _showResults = EditorGUILayout.Foldout(_showResults, "Results", true);

            if (!_showResults || _simulator.LastResult == null)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var result = _simulator.LastResult;

            // Summary
            Color successColor = result.SuccessRate >= 1f ? new Color(0.2f, 0.8f, 0.2f) :
                                 result.SuccessRate >= 0.8f ? new Color(0.9f, 0.7f, 0.1f) :
                                 new Color(0.9f, 0.2f, 0.2f);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Success Rate:", GUILayout.Width(100));
            GUI.color = successColor;
            EditorGUILayout.LabelField($"{result.SuccessRate * 100:F1}%", EditorStyles.boldLabel);
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Duration:", GUILayout.Width(100));
            EditorGUILayout.LabelField($"{result.Duration.TotalMilliseconds:F0} ms");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // Individual results
            EditorGUILayout.LabelField("Field Results:", EditorStyles.boldLabel);

            foreach (var fieldResult in result.ReconstructionResults)
            {
                EditorGUILayout.BeginHorizontal();

                // Status icon
                string icon = fieldResult.Reconstructed ? "✓" : "✗";
                Color iconColor = fieldResult.Reconstructed ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.9f, 0.2f, 0.2f);
                GUI.color = iconColor;
                EditorGUILayout.LabelField(icon, GUILayout.Width(20));
                GUI.color = Color.white;

                // Field info
                EditorGUILayout.LabelField($"{fieldResult.BehaviourName}.{fieldResult.FieldName}");

                if (!fieldResult.Reconstructed && !string.IsNullOrEmpty(fieldResult.Error))
                {
                    EditorGUILayout.LabelField(fieldResult.Error, EditorStyles.miniLabel, GUILayout.Width(150));
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        #endregion
    }
}

