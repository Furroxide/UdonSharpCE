using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharp.CE.Editor.DevTools.Analysis;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace UdonSharp.CE.Editor.DevTools.NetworkSim
{
    /// <summary>
    /// Replays an owner's synced state into running behaviours the way VRChat hands it to a joining
    /// client, and reports what each behaviour made of it.
    ///
    /// A late joiner is given the owner's last serialized value for every synced variable and then
    /// receives OnDeserialization. That is the whole of what this does: it writes the captured values
    /// into the backing UdonBehaviour and raises the event there. It runs in play mode only and never
    /// touches the C# proxies -- they hold the scene's authored values, so writing to them edits the
    /// scene rather than the running world.
    ///
    /// What it cannot model is the rest of a joiner's situation: a real joiner starts with fresh
    /// non-synced state, whereas the behaviours here keep whatever local state they have built up.
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
        /// The Udon event VRChat raises on a behaviour once its synced variables have been written.
        /// </summary>
        private const string DeserializationEvent = "_onDeserialization";

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

        /// <summary>
        /// How many captured behaviours still exist.
        ///
        /// A capture can outlive the play session it was taken in when domain reload is switched off;
        /// every object it refers to is destroyed by then, and such snapshots must not count as state
        /// that can be replayed.
        /// </summary>
        public int LiveSnapshotCount
        {
            get
            {
                int count = 0;

                foreach (var snapshot in CapturedSnapshots)
                {
                    if (IsLive(snapshot))
                        count++;
                }

                return count;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Captures the current value of every synced variable from the running UdonBehaviours.
        /// </summary>
        public void CaptureCurrentState()
        {
            CapturedSnapshots.Clear();

            // The live values are in the Udon heap, which only exists while the world is running. The C#
            // proxies this used to read hold the authored scene values in edit mode and a stale copy in
            // play mode, so a capture taken from them was never the state an owner would serialize.
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[LateJoinSimulator] Enter play mode before capturing: synced state lives in the running UdonBehaviours.");
                return;
            }

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
        /// Simulates a late join: every captured behaviour receives the captured values and then
        /// OnDeserialization, as it would from the owner.
        /// </summary>
        public LateJoinSimulationResult SimulateLateJoin(float delaySeconds = 0f)
        {
            // Never outside play mode. This used to write default(T) into every synced field of the
            // scene's proxies, with no Undo and nothing to put them back: run in edit mode it zeroed the
            // authored values, and the next scene save made that permanent.
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[LateJoinSimulator] Late-join simulation only runs in play mode.");
                return null;
            }

            if (LiveSnapshotCount == 0)
            {
                Debug.LogWarning("[LateJoinSimulator] No state captured. Call CaptureCurrentState first.");
                return null;
            }

            IsSimulating = true;

            var result = new LateJoinSimulationResult
            {
                StartTime = DateTime.Now,
                TotalBehaviours = LiveSnapshotCount,
                TotalSyncedFields = CapturedSnapshots.Where(IsLive).Sum(s => s.SyncedFields.Count)
            };

            // Wait for delay (simulating network latency)
            if (delaySeconds > 0)
            {
                result.SimulatedDelay = delaySeconds;
                // In a real scenario, we'd use coroutines or async
                // For editor, we'll note the delay but proceed immediately
            }

            try
            {
                // Deliver the owner's state and raise the event
                DeliverCapturedState();

                // Verify reconstruction
                result.ReconstructionResults = VerifyReconstruction();
                result.HaltedBehaviours = CapturedSnapshots.Count(s => IsLive(s) && s.Backing.HasError);
            }
            finally
            {
                // Whatever a handler did with them -- or however this method is left -- the synced
                // variables end as the captured values and never as something a failed run left behind.
                WriteCapturedState();
                IsSimulating = false;
            }

            result.EndTime = DateTime.Now;
            result.SuccessRate = result.ReconstructionResults.Count > 0
                ? (float)result.ReconstructionResults.Count(r => r.Reconstructed) / result.ReconstructionResults.Count
                : 0f;

            LastResult = result;

            Debug.Log($"[LateJoinSimulator] Simulation complete. Success rate: {result.SuccessRate * 100:F1}%");

            return result;
        }

        /// <summary>
        /// Writes the captured values back into the running behaviours.
        /// </summary>
        public void RestoreCapturedState()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[LateJoinSimulator] Captured state can only be restored in play mode.");
                return;
            }

            if (LiveSnapshotCount == 0)
            {
                Debug.LogWarning("[LateJoinSimulator] No state captured to restore.");
                return;
            }

            int restoredCount = WriteCapturedState();

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

                // A None / NoVariableSync behaviour transmits no variables at all, so its [UdonSynced]
                // fields belong in their own bucket -- they are not fields a late joiner receives, they
                // are fields that never replicate.
                bool syncsVariables =
                    BehaviourAnalyzer.ModeSyncsVariables(BehaviourAnalyzer.ResolveSyncMode(behaviour));

                if (!syncsVariables)
                    analysis.BehavioursWithoutVariableSync++;

                foreach (var field in GetInstanceFields(behaviourType))
                {
                    // Counted on its own, not as a share of the [UdonSynced] fields. [SyncOnJoin] goes
                    // on a plain field and CE generates the synced payload that carries it; looking for
                    // it only on fields that were already [UdonSynced] reported no coverage for exactly
                    // the fields that use it, and then advised adding it to fields VRChat already
                    // delivers to late joiners.
                    if (HasAttributeNamed(field, "SyncOnJoinAttribute"))
                        analysis.SyncOnJoinFields++;

                    // Check for UdonSynced attribute
                    if (field.GetCustomAttribute<UdonSyncedAttribute>() == null)
                        continue;

                    if (!syncsVariables)
                    {
                        analysis.NonReplicatingSyncedFields++;
                        continue;
                    }

                    analysis.TotalSyncedFields++;
                }

                // Check for OnPlayerJoined handler
                if (FindUserOverride(behaviourType, "OnPlayerJoined", 1) != null)
                {
                    analysis.OnPlayerJoinedHandlers++;
                }

                // Check for OnDeserialization handler, in either of its two signatures
                if (FindUserOverride(behaviourType, "OnDeserialization", 0) != null ||
                    FindUserOverride(behaviourType, "OnDeserialization", 1) != null)
                {
                    analysis.OnDeserializationHandlers++;
                }
            }

            analysis.TotalBehaviours = behaviours.Length;

            return analysis;
        }

        #endregion

        #region Reflection Helpers

        /// <summary>
        /// Finds a behaviour's own implementation of a UdonSharpBehaviour event, or null if it has none.
        ///
        /// A lookup by name alone cannot answer this. UdonSharpBehaviour declares OnDeserialization twice
        /// (with and without a DeserializationResult), so Type.GetMethod("OnDeserialization") throws
        /// AmbiguousMatchException for every behaviour there is; and for an event declared once, such as
        /// OnPlayerJoined, the same lookup returns the empty base virtual, which reports a handler on
        /// behaviours that have none. Walking only the user's own types, by name and parameter count,
        /// avoids both.
        /// </summary>
        internal static MethodInfo FindUserOverride(Type behaviourType, string methodName, int parameterCount)
        {
            const BindingFlags declaredInstance =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            for (Type type = behaviourType; type != null && type != typeof(UdonSharpBehaviour); type = type.BaseType)
            {
                foreach (MethodInfo method in type.GetMethods(declaredInstance))
                {
                    if (method.Name == methodName && method.GetParameters().Length == parameterCount)
                        return method;
                }
            }

            return null;
        }

        /// <summary>
        /// Every instance field a behaviour carries, including private ones declared on its base classes.
        ///
        /// Type.GetFields on the derived type leaves those out, so a [UdonSynced] private field on a
        /// shared base class was invisible here even though it is synced like any other.
        /// </summary>
        internal static List<FieldInfo> GetInstanceFields(Type behaviourType)
        {
            const BindingFlags declaredInstance =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var fields = new List<FieldInfo>();

            for (Type type = behaviourType; type != null && type != typeof(UdonSharpBehaviour); type = type.BaseType)
            {
                fields.AddRange(type.GetFields(declaredInstance));
            }

            return fields;
        }

        private static bool HasAttributeNamed(FieldInfo field, string attributeTypeName)
        {
            return field.GetCustomAttributes().Any(a => a.GetType().Name == attributeTypeName);
        }

        #endregion

        #region Internal Methods

        private static bool IsLive(BehaviourSnapshot snapshot)
        {
            return snapshot != null && snapshot.Behaviour != null && snapshot.Backing != null;
        }

        private BehaviourSnapshot CaptureBehaviourState(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return null;

            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(behaviour);
            if (backing == null)
                return null;

            var snapshot = new BehaviourSnapshot
            {
                Behaviour = behaviour,
                Backing = backing,
                BehaviourType = behaviour.GetType(),
                GameObjectName = behaviour.gameObject.name,
                BehaviourSyncMode = BehaviourAnalyzer.ResolveSyncMode(behaviour)
            };

            // Fields on a None / NoVariableSync behaviour never leave the local client, so replaying
            // them measures nothing but local assignment and would report a success rate that has no
            // bearing on what a real late joiner sees.
            if (!BehaviourAnalyzer.ModeSyncsVariables(snapshot.BehaviourSyncMode))
                return null;

            foreach (var field in GetInstanceFields(snapshot.BehaviourType))
            {
                var syncAttr = field.GetCustomAttribute<UdonSyncedAttribute>();
                if (syncAttr == null)
                    continue;

                try
                {
                    // Read from the heap, in the heap's own representation, and written back the same
                    // way: nothing here has to know how U# stores an enum or a user type.
                    if (!backing.TryGetProgramVariable(field.Name, out object value))
                        continue;

                    snapshot.SyncedFields.Add(new FieldSnapshot
                    {
                        FieldName = field.Name,
                        FieldType = field.FieldType,
                        CapturedValue = CloneValue(value),
                        InterpolationMode = syncAttr.NetworkSyncType.ToString()
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LateJoinSimulator] Failed to capture {field.Name}: {ex.Message}");
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Hands every captured behaviour the owner's values and raises OnDeserialization on it.
        /// </summary>
        private void DeliverCapturedState()
        {
            foreach (var snapshot in CapturedSnapshots)
            {
                if (!IsLive(snapshot))
                    continue;

                // A behaviour that has already halted runs nothing. Verification reports it as failed
                // rather than letting its untouched variables read as a successful reconstruction.
                if (snapshot.Backing.HasError)
                    continue;

                WriteSnapshot(snapshot);

                try
                {
                    // On the UdonBehaviour, where the program actually runs. Invoking the proxy's C#
                    // method instead would execute the handler in the editor against the proxy's fields
                    // and leave the running world untouched. A behaviour with no handler has no such
                    // entry point and this does nothing.
                    snapshot.Backing.SendCustomEvent(DeserializationEvent);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LateJoinSimulator] OnDeserialization failed for {snapshot.GameObjectName}: {ex.Message}");
                }
            }

            Debug.Log("[LateJoinSimulator] Delivered captured state and raised OnDeserialization");
        }

        /// <summary>
        /// Writes every captured value into its behaviour. Returns how many fields were written.
        /// </summary>
        private int WriteCapturedState()
        {
            int written = 0;

            foreach (var snapshot in CapturedSnapshots)
            {
                if (!IsLive(snapshot))
                    continue;

                written += WriteSnapshot(snapshot);
            }

            return written;
        }

        private int WriteSnapshot(BehaviourSnapshot snapshot)
        {
            int written = 0;

            foreach (var field in snapshot.SyncedFields)
            {
                try
                {
                    // A fresh copy each time: the heap must never share an array with the snapshot, or
                    // the program writing into it would rewrite what was captured.
                    if (WriteProgramVariable(snapshot.Backing, field.FieldName, CloneValue(field.CapturedValue)))
                        written++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LateJoinSimulator] Failed to write {field.FieldName}: {ex.Message}");
                }
            }

            return written;
        }

        private static MethodInfo _typedSetProgramVariable;

        /// <summary>
        /// Writes a heap variable as the type its slot already holds.
        ///
        /// SetProgramVariable(string, object) is not that: it stores through SetHeapVariable&lt;object&gt;,
        /// and the heap keeps a value in place only when the slot is already a StrongBox of the same T,
        /// so every int, float or Vector3 slot written that way was replaced with a StrongBox&lt;object&gt;
        /// and reported its type as object from then on. Writing through SetProgramVariable&lt;T&gt; with
        /// the slot's own T leaves the slot as the compiled program declared it.
        /// </summary>
        private static bool WriteProgramVariable(UdonBehaviour behaviour, string symbolName, object value)
        {
            Type slotType = behaviour.GetProgramVariableType(symbolName);
            if (slotType == null)
                return false;

            // A value the slot cannot hold is not written. It came out of this slot, so a mismatch means
            // the program was recompiled since the capture.
            if (value == null ? slotType.IsValueType : !slotType.IsInstanceOfType(value))
                return false;

            if (_typedSetProgramVariable == null)
            {
                _typedSetProgramVariable = typeof(UdonBehaviour)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == nameof(UdonBehaviour.SetProgramVariable) &&
                                              method.IsGenericMethodDefinition &&
                                              method.GetParameters().Length == 2);
            }

            if (_typedSetProgramVariable == null)
            {
                behaviour.SetProgramVariable(symbolName, value);
                return true;
            }

            try
            {
                _typedSetProgramVariable.MakeGenericMethod(slotType).Invoke(behaviour, new[] { symbolName, value });
            }
            catch (TargetInvocationException invocationFailure) when (invocationFailure.InnerException != null)
            {
                // Reported by the caller as the write that failed, not as a reflection error.
                throw invocationFailure.InnerException;
            }

            return true;
        }

        private List<FieldReconstructionResult> VerifyReconstruction()
        {
            var results = new List<FieldReconstructionResult>();

            foreach (var snapshot in CapturedSnapshots)
            {
                if (!IsLive(snapshot))
                    continue;

                bool halted = snapshot.Backing.HasError;

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
                        if (snapshot.Backing.TryGetProgramVariable(field.FieldName, out object current))
                        {
                            result.CurrentValue = current;
                            result.Reconstructed = ValuesEqual(result.OriginalValue, result.CurrentValue);
                        }

                        if (halted)
                        {
                            // The values may well still match -- nothing ran to change them.
                            result.Reconstructed = false;
                            result.Error = "behaviour halted";
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

        private static object CloneValue(object value)
        {
            if (value == null)
                return null;

            // Handle arrays
            if (value is Array array)
            {
                return array.Clone();
            }

            // Value types are copied by value, strings are immutable, and other reference types cannot
            // be deep cloned in general -- the reference is all there is to hand back.
            return value;
        }

        private static bool ValuesEqual(object a, object b)
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

        /// <summary>
        /// The UdonBehaviour that runs <see cref="Behaviour"/>. Synced state is read from and written to
        /// its heap; the proxy is only how the behaviour was found.
        /// </summary>
        public UdonBehaviour Backing;

        public Type BehaviourType;
        public string GameObjectName;
        public List<FieldSnapshot> SyncedFields = new List<FieldSnapshot>();

        /// <summary>
        /// The behaviour's resolved [UdonBehaviourSyncMode] (Continuous when none is declared, because
        /// BehaviourSyncMode.Any is what the UdonBehaviour resolves to Continuous at runtime).
        /// </summary>
        public string BehaviourSyncMode;
    }

    /// <summary>
    /// Snapshot of a single synced field.
    /// </summary>
    public class FieldSnapshot
    {
        public string FieldName;
        public Type FieldType;
        public object CapturedValue;

        /// <summary>
        /// The [UdonSynced] interpolation mode (None / Linear / Smooth). This is per-field smoothing, not
        /// the behaviour's [UdonBehaviourSyncMode]; it was called SyncMode, which invited exactly the
        /// confusion between the two that made these tools report the wrong thing.
        /// </summary>
        public string InterpolationMode;
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

        /// <summary>
        /// Behaviours whose UdonBehaviour was halted when the run finished, whether OnDeserialization
        /// threw or it had stopped earlier. Their fields are reported as not reconstructed.
        /// </summary>
        public int HaltedBehaviours;

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

        /// <summary>
        /// Fields marked [SyncOnJoin], whatever else they carry. This is a count in its own right and not
        /// a share of <see cref="TotalSyncedFields"/>: the attribute belongs on fields that are not
        /// [UdonSynced], so the two sets barely overlap.
        /// </summary>
        public int SyncOnJoinFields;

        public int OnPlayerJoinedHandlers;
        public int OnDeserializationHandlers;

        /// <summary>
        /// Behaviours whose resolved [UdonBehaviourSyncMode] is None or NoVariableSync.
        /// </summary>
        public int BehavioursWithoutVariableSync;

        /// <summary>
        /// [UdonSynced] fields declared on those behaviours. They never reach any remote player, late
        /// joiner or not, so they are reported separately from the fields that do.
        /// </summary>
        public int NonReplicatingSyncedFields;
    }

    #endregion

    /// <summary>
    /// Editor window for the late-join simulator.
    /// </summary>
    public class LateJoinSimulatorWindow : EditorWindow
    {
        #region Menu Item

        [MenuItem("Udon CE/Dev Tools/Late-Join Simulator", false, 1404)]
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
                "Replays the owner's synced state into running behaviours the way VRChat hands it to a " +
                "player who joins mid-session. Capture and Simulate need play mode.\n\n" +
                "1. Capture the synced variables of every running behaviour\n" +
                "2. Simulate: each behaviour receives the captured values, then OnDeserialization\n" +
                "3. Check that every behaviour kept the delivered state and did not halt\n\n" +
                "A real joiner also starts with fresh non-synced state. That part is not simulated.",
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
                EditorGUILayout.LabelField(_lastAnalysis.SyncOnJoinFields.ToString());
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("OnPlayerJoined Handlers:", GUILayout.Width(150));
                EditorGUILayout.LabelField(_lastAnalysis.OnPlayerJoinedHandlers.ToString());
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("OnDeserialization Handlers:", GUILayout.Width(150));
                EditorGUILayout.LabelField(_lastAnalysis.OnDeserializationHandlers.ToString());
                EditorGUILayout.EndHorizontal();

                if (_lastAnalysis.NonReplicatingSyncedFields > 0)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox(
                        $"{_lastAnalysis.NonReplicatingSyncedFields} [UdonSynced] field(s) sit on " +
                        $"{_lastAnalysis.BehavioursWithoutVariableSync} behaviour(s) whose sync mode is None or " +
                        "NoVariableSync. Those fields never replicate to anyone, so no amount of late-join " +
                        "handling will populate them.",
                        MessageType.Warning);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawCaptureControls()
        {
            EditorGUILayout.LabelField("State Capture", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool isPlaying = EditorApplication.isPlaying;
            int liveSnapshots = _simulator.LiveSnapshotCount;

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = isPlaying;
            if (GUILayout.Button("Capture Current State", GUILayout.Height(30)))
            {
                _simulator.CaptureCurrentState();
            }

            GUI.enabled = isPlaying && liveSnapshots > 0;
            if (GUILayout.Button("Restore Captured State", GUILayout.Height(30)))
            {
                _simulator.RestoreCapturedState();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            if (!isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter play mode to capture. Synced state lives in the running UdonBehaviours; in edit " +
                    "mode there is only the scene's authored data, which this tool does not modify.",
                    MessageType.Info);
            }

            // Show capture stats
            if (liveSnapshots > 0)
            {
                EditorGUILayout.Space(5);
                int totalFields = 0;
                foreach (var snapshot in _simulator.CapturedSnapshots)
                {
                    if (snapshot != null && snapshot.Behaviour != null && snapshot.Backing != null)
                        totalFields += snapshot.SyncedFields.Count;
                }

                EditorGUILayout.LabelField(
                    $"Captured: {liveSnapshots} behaviours, {totalFields} fields",
                    EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSimulationControls()
        {
            EditorGUILayout.LabelField("Simulation", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool isPlaying = EditorApplication.isPlaying;
            int liveSnapshots = _simulator.LiveSnapshotCount;

            // Delay setting
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Simulated Delay (s):", GUILayout.Width(130));
            _simulatedDelay = EditorGUILayout.Slider(_simulatedDelay, 0f, 5f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            GUI.enabled = isPlaying && liveSnapshots > 0 && !_simulator.IsSimulating;

            if (GUILayout.Button("Simulate Late-Join", GUILayout.Height(35)))
            {
                _simulator.SimulateLateJoin(_simulatedDelay);
            }

            GUI.enabled = true;

            if (isPlaying && liveSnapshots == 0)
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

            if (result.HaltedBehaviours > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{result.HaltedBehaviours} behaviour(s) were halted when the run finished. A halted " +
                    "UdonBehaviour runs nothing until play mode is restarted; the console has the exception.",
                    MessageType.Error);
            }

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
