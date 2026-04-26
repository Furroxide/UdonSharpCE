using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Status of the late joiner sync process.
    /// </summary>
    [PublicAPI]
    public enum LateJoinerSyncStatus
    {
        /// <summary>
        /// No sync in progress.
        /// </summary>
        Idle,

        /// <summary>
        /// Sync is in progress.
        /// </summary>
        Syncing,

        /// <summary>
        /// Sync completed successfully.
        /// </summary>
        Complete,

        /// <summary>
        /// Sync failed (timeout or error).
        /// </summary>
        Failed
    }

    /// <summary>
    /// Manages synchronization of [SyncOnJoin] fields to late joiners.
    ///
    /// Place this component in your scene and register behaviours that
    /// have [SyncOnJoin] fields. When a new player joins, the master
    /// client will send all registered data.
    /// </summary>
    /// <remarks>
    /// Sync Process:
    /// 1. Player joins the instance
    /// 2. Master detects join via OnPlayerJoined
    /// 3. Master serializes registered [SyncOnJoin] fields
    /// 4. Data is sent via synced string array
    /// 5. Late joiner deserializes and applies data
    ///
    /// Data is sent in priority order with time-slicing to avoid
    /// network congestion and frame spikes.
    /// </remarks>
    /// <example>
    /// <code>
    /// // In scene setup
    /// public LateJoinerSync lateJoinerSync;
    /// public GameManager gameManager;
    /// public WorldState worldState;
    ///
    /// void Start()
    /// {
    ///     lateJoinerSync.RegisterBehaviour(gameManager);
    ///     lateJoinerSync.RegisterBehaviour(worldState);
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class LateJoinerSync : UdonSharpBehaviour
    {
        #region Configuration

        /// <summary>
        /// Maximum behaviours that can be registered.
        /// </summary>
        private const int MAX_BEHAVIOURS = 32;

        /// <summary>
        /// Maximum sync data size per chunk (characters).
        /// </summary>
        private const int MAX_CHUNK_SIZE = 16000;

        /// <summary>
        /// Maximum number of chunks allowed per sync.
        /// </summary>
        private const int MAX_CHUNKS = 100;

        /// <summary>
        /// Delay between sync chunks in seconds.
        /// </summary>
        private const float CHUNK_DELAY = 0.2f;

        /// <summary>
        /// Timeout for sync completion in seconds.
        /// </summary>
        private const float SYNC_TIMEOUT = 30f;

        private const string SyncOnJoinPayloadField = "_CE_SyncOnJoinPayload";
        private const string SyncOnJoinInboundField = "_CE_SyncOnJoinInbound";
        private const string SyncOnJoinSerializeEvent = "_CE_SyncOnJoin_Serialize";
        private const string SyncOnJoinDeserializeEvent = "_CE_SyncOnJoin_Deserialize_Trigger";

        #endregion

        #region Synced Variables

        /// <summary>
        /// Target player ID for current sync operation.
        /// </summary>
        [UdonSynced]
        private int _targetPlayerId = -1;

        /// <summary>
        /// Current sync sequence number.
        /// </summary>
        [UdonSynced]
        private int _syncSequence;

        /// <summary>
        /// Total chunks in current sync.
        /// </summary>
        [UdonSynced]
        private int _totalChunks;

        /// <summary>
        /// Current chunk index.
        /// </summary>
        [UdonSynced]
        private int _currentChunk;

        /// <summary>
        /// Sync data payload.
        /// </summary>
        [UdonSynced]
        private string _syncData = "";

        #endregion

        #region Local State

        /// <summary>
        /// Registered behaviours with [SyncOnJoin] fields.
        /// </summary>
        private UdonSharpBehaviour[] _behaviours;

        /// <summary>
        /// Number of registered behaviours.
        /// </summary>
        private int _behaviourCount;

        /// <summary>
        /// Priority values for sorting.
        /// </summary>
        private int[] _priorities;

        /// <summary>
        /// Stable registration identifiers used to route payloads back to the correct behaviour.
        /// </summary>
        private string[] _behaviourIds;

        /// <summary>
        /// Current sync status.
        /// </summary>
        public LateJoinerSyncStatus Status { get; private set; }

        /// <summary>
        /// Sync progress (0 to 1).
        /// </summary>
        public float Progress { get; private set; }

        /// <summary>
        /// Player currently being synced.
        /// </summary>
        private VRCPlayerApi _syncingPlayer;

        /// <summary>
        /// Time sync started for timeout.
        /// </summary>
        private float _syncStartTime;

        /// <summary>
        /// Local sequence for detecting changes.
        /// </summary>
        private int _lastReceivedSequence = -1;

        /// <summary>
        /// Buffer for assembling chunked data.
        /// </summary>
        private string[] _chunkBuffer;

        /// <summary>
        /// Number of chunks received.
        /// </summary>
        private int _chunksReceived;

        /// <summary>
        /// Tracks which chunks have been received for the current sync.
        /// </summary>
        private bool[] _chunkReceived;

        /// <summary>
        /// Snapshot of chunks being sent by the master.
        /// </summary>
        private string[] _currentChunks;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            _behaviours = new UdonSharpBehaviour[MAX_BEHAVIOURS];
            _priorities = new int[MAX_BEHAVIOURS];
            _behaviourIds = new string[MAX_BEHAVIOURS];
            _chunkBuffer = new string[MAX_CHUNKS];
            _chunkReceived = new bool[MAX_CHUNKS];
            Status = LateJoinerSyncStatus.Idle;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Registers a behaviour for late joiner synchronization.
        /// </summary>
        /// <param name="behaviour">Behaviour with [SyncOnJoin] fields.</param>
        /// <param name="priority">Sync priority (lower = earlier).</param>
        public void RegisterBehaviour(UdonSharpBehaviour behaviour, int priority = 100)
        {
            if (behaviour == null || _behaviourCount >= MAX_BEHAVIOURS)
                return;

            // Check if already registered
            for (int i = 0; i < _behaviourCount; i++)
            {
                if (_behaviours[i] == behaviour)
                    return;
            }

            _behaviours[_behaviourCount] = behaviour;
            _priorities[_behaviourCount] = priority;
            _behaviourIds[_behaviourCount] = BuildBehaviourRegistrationId(behaviour);
            _behaviourCount++;

            // Sort by priority
            SortByPriority();
        }

        /// <summary>
        /// Unregisters a behaviour from late joiner synchronization.
        /// </summary>
        public void UnregisterBehaviour(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return;

            for (int i = 0; i < _behaviourCount; i++)
            {
                if (_behaviours[i] == behaviour)
                {
                    // Shift remaining elements
                    for (int j = i; j < _behaviourCount - 1; j++)
                    {
                        _behaviours[j] = _behaviours[j + 1];
                        _priorities[j] = _priorities[j + 1];
                        _behaviourIds[j] = _behaviourIds[j + 1];
                    }
                    _behaviourCount--;
                    _behaviours[_behaviourCount] = null;
                    _behaviourIds[_behaviourCount] = null;
                    return;
                }
            }
        }

        /// <summary>
        /// Forces a sync to a specific player.
        /// Only works when called by the master.
        /// </summary>
        public void ForceSyncToPlayer(VRCPlayerApi player)
        {
            if (player == null || !Networking.IsMaster)
                return;

            StartSyncToPlayer(player);
        }

        #endregion

        #region VRChat Events

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Only master initiates sync
            if (!Networking.IsMaster)
                return;

            // Don't sync to ourselves
            if (player.isLocal)
                return;

            // Start sync process
            StartSyncToPlayer(player);
        }

        public override void OnDeserialization()
        {
            // Check if this is a new sync sequence
            if (_syncSequence == _lastReceivedSequence)
                return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return;

            // Check if we're the target
            if (_targetPlayerId != localPlayer.playerId)
                return;

            if (_totalChunks > _chunkBuffer.Length)
            {
                Debug.LogError($"[LateJoinerSync] Sync exceeded buffer size ({_totalChunks} chunks, max {_chunkBuffer.Length})");
                return;
            }

            if (_currentChunk == 0)
            {
                ResetReceiveBuffer();
            }

            // Store chunk if it hasn't been received yet
            if (_currentChunk >= 0 && _currentChunk < _chunkBuffer.Length)
            {
                if (!_chunkReceived[_currentChunk])
                {
                    _chunkBuffer[_currentChunk] = _syncData;
                    _chunkReceived[_currentChunk] = true;
                    _chunksReceived++;
                }

                int expectedChunks = Mathf.Max(1, _totalChunks);
                Progress = (float)_chunksReceived / expectedChunks;

                // Check if all chunks received
                if (_chunksReceived >= _totalChunks)
                {
                    ProcessReceivedData();
                }
            }

            _lastReceivedSequence = _syncSequence;
        }

        #endregion

        #region Master Sync Logic

        /// <summary>
        /// Starts sync process to a player.
        /// </summary>
        private void StartSyncToPlayer(VRCPlayerApi player)
        {
            if (_behaviourCount == 0)
                return;

            _syncingPlayer = player;
            _syncStartTime = Time.time;
            Status = LateJoinerSyncStatus.Syncing;
            Progress = 0f;

            // Serialize all registered behaviours
            string fullData = SerializeAllBehaviours();

            // Split into chunks
            _currentChunks = SplitIntoChunks(fullData, MAX_CHUNK_SIZE);

            // Start sending
            _targetPlayerId = player.playerId;
            _totalChunks = _currentChunks.Length;
            _currentChunk = 0;
            _syncSequence++;

            SendNextChunk();
        }

        /// <summary>
        /// Sends the next chunk of sync data.
        /// </summary>
        private void SendNextChunk()
        {
            if (_currentChunks == null)
                return;

            if (_currentChunk >= _currentChunks.Length)
            {
                // Sync complete
                Status = LateJoinerSyncStatus.Complete;
                Progress = 1f;
                _syncingPlayer = null;
                return;
            }

            _syncData = _currentChunks[_currentChunk];
            RequestSerialization();

            Progress = (float)_currentChunk / _currentChunks.Length;

            _currentChunk++;
            _syncSequence++;

            // Schedule next chunk
            SendCustomEventDelayedSeconds(nameof(_ContinueSync), CHUNK_DELAY);
        }

        /// <summary>
        /// Continues sending chunks.
        /// </summary>
        public void _ContinueSync()
        {
            if (Status != LateJoinerSyncStatus.Syncing)
                return;

            // Check timeout
            if (Time.time - _syncStartTime > SYNC_TIMEOUT)
            {
                Status = LateJoinerSyncStatus.Failed;
                _syncingPlayer = null;
                return;
            }

            SendNextChunk();
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes all registered behaviours to a string.
        /// </summary>
        private string SerializeAllBehaviours()
        {
            string result = "";

            for (int i = 0; i < _behaviourCount; i++)
            {
                var behaviour = _behaviours[i];
                if (behaviour == null)
                    continue;

                string key = _behaviourIds[i];
                string data = SerializeBehaviour(behaviour);

                result += $"{key}|{data}\n";
            }

            return result;
        }

        /// <summary>
        /// Serializes a single behaviour's [SyncOnJoin] fields.
        /// </summary>
        private string SerializeBehaviour(UdonSharpBehaviour behaviour)
        {
            UdonBehaviour udonBehaviour = GetBackingUdonBehaviour(behaviour);
            if (udonBehaviour == null)
            {
                Debug.LogError($"[LateJoinerSync] Failed to find backing UdonBehaviour for '{behaviour}'.");
                return "";
            }

            udonBehaviour.SendCustomEvent(SyncOnJoinSerializeEvent);

            object payload = udonBehaviour.GetProgramVariable(SyncOnJoinPayloadField);
            if (payload == null)
                return "";

            if (payload is string payloadString)
                return payloadString;

            Debug.LogError($"[LateJoinerSync] Generated payload field on '{behaviour.name}' was not a string.");
            return "";
        }

        /// <summary>
        /// Splits data into chunks.
        /// </summary>
        private string[] SplitIntoChunks(string data, int chunkSize)
        {
            if (string.IsNullOrEmpty(data))
                return new string[] { "" };

            int chunkCount = (data.Length + chunkSize - 1) / chunkSize;
            string[] chunks = new string[chunkCount];

            for (int i = 0; i < chunkCount; i++)
            {
                int start = i * chunkSize;
                int length = Mathf.Min(chunkSize, data.Length - start);
                chunks[i] = data.Substring(start, length);
            }

            return chunks;
        }

        #endregion

        #region Deserialization

        /// <summary>
        /// Processes received sync data.
        /// </summary>
        private void ProcessReceivedData()
        {
            // Reassemble chunks
            string fullData = "";
            int totalChunks = Mathf.Min(_totalChunks, _chunkBuffer.Length);
            for (int i = 0; i < totalChunks; i++)
            {
                if (_chunkBuffer[i] != null)
                {
                    fullData += _chunkBuffer[i];
                    _chunkBuffer[i] = null;
                }
            }
            ResetReceiveBuffer();

            // Parse and apply to behaviours
            DeserializeAllBehaviours(fullData);

            Status = LateJoinerSyncStatus.Complete;
            Progress = 1f;

            Debug.Log($"[LateJoinerSync] Sync complete, received {fullData.Length} characters");
        }

        /// <summary>
        /// Deserializes and applies data to registered behaviours.
        /// </summary>
        private void DeserializeAllBehaviours(string data)
        {
            string[] lines = data.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line))
                    continue;

                int separator = line.IndexOf('|');
                if (separator < 0)
                    continue;

                string key = line.Substring(0, separator);
                string payload = line.Substring(separator + 1);

                // Find matching behaviour
                for (int j = 0; j < _behaviourCount; j++)
                {
                    if (_behaviours[j] != null && _behaviourIds[j] == key)
                    {
                        DeserializeBehaviour(_behaviours[j], payload);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Deserializes data to a behaviour's [SyncOnJoin] fields.
        /// </summary>
        private void DeserializeBehaviour(UdonSharpBehaviour behaviour, string data)
        {
            UdonBehaviour udonBehaviour = GetBackingUdonBehaviour(behaviour);
            if (udonBehaviour == null)
            {
                Debug.LogError($"[LateJoinerSync] Failed to find backing UdonBehaviour for '{behaviour}'.");
                return;
            }

            udonBehaviour.SetProgramVariable(SyncOnJoinInboundField, data ?? "");
            udonBehaviour.SendCustomEvent(SyncOnJoinDeserializeEvent);
        }

        private void ResetReceiveBuffer()
        {
            for (int i = 0; i < _chunkBuffer.Length; i++)
            {
                _chunkBuffer[i] = null;
                _chunkReceived[i] = false;
            }

            _chunksReceived = 0;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Sorts registered behaviours by priority.
        /// </summary>
        private void SortByPriority()
        {
            // Simple bubble sort (small array)
            for (int i = 0; i < _behaviourCount - 1; i++)
            {
                for (int j = 0; j < _behaviourCount - i - 1; j++)
                {
                    if (_priorities[j] > _priorities[j + 1])
                    {
                        // Swap
                        var tempBehaviour = _behaviours[j];
                        _behaviours[j] = _behaviours[j + 1];
                        _behaviours[j + 1] = tempBehaviour;

                        string tempBehaviourId = _behaviourIds[j];
                        _behaviourIds[j] = _behaviourIds[j + 1];
                        _behaviourIds[j + 1] = tempBehaviourId;

                        int tempPriority = _priorities[j];
                        _priorities[j] = _priorities[j + 1];
                        _priorities[j + 1] = tempPriority;
                    }
                }
            }
        }

        private static string BuildBehaviourRegistrationId(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return string.Empty;

            UdonBehaviour backingBehaviour = GetBackingUdonBehaviour(behaviour);
            int componentIndex = GetBackingBehaviourIndex(backingBehaviour);
            return $"{BuildHierarchyPath(behaviour.transform)}#{componentIndex}";
        }

        private static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = string.Empty;
            Transform current = transform;
            while (current != null)
            {
                path = "/" + current.GetSiblingIndex() + path;
                current = current.parent;
            }

            return path;
        }

        private static int GetBackingBehaviourIndex(UdonBehaviour backingBehaviour)
        {
            if (backingBehaviour == null)
                return -1;

            UdonBehaviour[] behaviours = backingBehaviour.GetComponents<UdonBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == backingBehaviour)
                    return i;
            }

            return -1;
        }

        private static UdonBehaviour GetBackingUdonBehaviour(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return null;

            UdonBehaviour backingBehaviour = behaviour.GetComponent<UdonBehaviour>();
            if (backingBehaviour != null)
                return backingBehaviour;

            return (UdonBehaviour)(Component)behaviour;
        }

        #endregion
    }
}
