using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Central coordinator for a networked physics simulation.
    /// Provides a fixed-timestep tick counter, entity registry, and state history.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class NetPhysicsWorld : UdonSharpBehaviour
    {
        [Header("Simulation")]
        public int TickRate = 60;
        public int MaxTicksPerFrame = 4;
        public bool AutoSimulate = true;

        [Header("History")]
        public int HistorySize = 128;
        public int MaxEntities = 64;

        [Header("Input (Client)")]
        public InputRecorder LocalInputRecorder;

        [Header("Sync (Master)")]
        public float StateSendRate = 15f;
        public int MaxEntitiesPerStatePacket = 8;

        public bool IsMaster => Networking.IsMaster;

        public int CurrentFrame { get; private set; }
        public float SimulationTime => _clock != null ? _clock.SimulationTime : 0f;
        public int EntityCount => _entityCount;

        public NetPhysicsEntity[] Entities => _entities;

        private PhysicsClock _clock;
        private FrameHistory _history;
        private PhysicsSnapshot _tempSnapshot;
        private InputBuffer _inputBuffer;
        private InputPredictor _inputPredictor;

        // Max state data size: 512 bytes covers typical world state for a few vehicles
        [UdonSynced] private byte[] _stateData = new byte[512];
        [UdonSynced] private int _stateSequence;

        private int _lastProcessedStateSequence = int.MinValue;
        private float _lastStateSendTime;
        private int _stateChunkCursor;
        private StateCompressor _stateCompressor;
        private PhysicsSnapshot _receivedSnapshot;
        private PhysicsSnapshot _broadcastSnapshot;
        private RollbackManager _rollbackManager;

        private NetPhysicsEntity[] _entities;
        private int _entityCount;

        private void Start()
        {
            _entities = new NetPhysicsEntity[Mathf.Max(1, MaxEntities)];
            _clock = new PhysicsClock(TickRate);
            _history = new FrameHistory(Mathf.Max(1, HistorySize), _entities.Length);
            _tempSnapshot = new PhysicsSnapshot(_entities.Length);
            _inputBuffer = new InputBuffer();
            _inputPredictor = new InputPredictor();

            _stateCompressor = new StateCompressor();
            _receivedSnapshot = new PhysicsSnapshot(_entities.Length);
            _broadcastSnapshot = new PhysicsSnapshot(_entities.Length);
            _rollbackManager = new RollbackManager();
            _rollbackManager.Initialize(this, _history);

            if (IsMaster && !Networking.IsOwner(gameObject))
            {
                var local = Networking.LocalPlayer;
                if (local != null && local.IsValid())
                    Networking.SetOwner(local, gameObject);
            }
        }

        private void FixedUpdate()
        {
            if (AutoSimulate)
                Simulate();

            if (IsMaster && StateSendRate > 0f && Time.time - _lastStateSendTime >= 1f / StateSendRate)
            {
                BroadcastState();
                _lastStateSendTime = Time.time;
            }
        }

        public void RegisterEntity(NetPhysicsEntity entity)
        {
            if (entity == null)
                return;

            // Assign an ID if needed.
            if (entity.EntityId < 0)
            {
                int assigned = FindNextFreeEntityId();
                entity.SetEntityIdInternal(assigned);
            }

            // Prevent duplicates.
            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] == entity || _entities[i].EntityId == entity.EntityId)
                    return;
            }

            EnsureEntityCapacity(_entityCount + 1);

            // Insert sorted by EntityId for stable snapshot ordering.
            int insertIndex = _entityCount;
            while (insertIndex > 0 && _entities[insertIndex - 1].EntityId > entity.EntityId)
            {
                _entities[insertIndex] = _entities[insertIndex - 1];
                insertIndex--;
            }

            _entities[insertIndex] = entity;
            _entityCount++;

            // CELogger.Debug("NetPhysics", $"Registered entity {entity.name} (id={entity.EntityId})");
        }

        public void UnregisterEntity(NetPhysicsEntity entity)
        {
            if (entity == null)
                return;

            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] != entity)
                    continue;

                for (int j = i; j < _entityCount - 1; j++)
                {
                    _entities[j] = _entities[j + 1];
                }

                _entities[_entityCount - 1] = null;
                _entityCount--;

                // CELogger.Debug("NetPhysics", $"Unregistered entity {entity.name} (id={entity.EntityId})");
                return;
            }
        }

        public NetPhysicsEntity GetEntity(int entityId)
        {
            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] != null && _entities[i].EntityId == entityId)
                    return _entities[i];
            }

            return null;
        }

        /// <summary>
        /// Main simulation entry point. Intended to be called from FixedUpdate.
        /// </summary>
        public void Simulate()
        {
            if (_clock == null)
                _clock = new PhysicsClock(TickRate);

            int ticks = _clock.ConsumeTicks(Time.fixedDeltaTime, MaxTicksPerFrame);
            for (int i = 0; i < ticks; i++)
            {
                InputFrame localInput = new InputFrame();
                if (!IsMaster && LocalInputRecorder != null)
                {
                    localInput = LocalInputRecorder.CurrentInput;
                    localInput.FrameNumber = CurrentFrame;
                }

                SimulateSingleTick(localInput);
            }
        }

        /// <summary>
        /// Simulates a single tick. Used for rollback resimulation.
        /// </summary>
        public void SimulateSingleTick(InputFrame input)
        {
            int frame = CurrentFrame;

            // Apply per-entity simulation for this tick.
            for (int i = 0; i < _entityCount; i++)
            {
                var vehicle = _entities[i] as NetVehicle;
                if (vehicle == null)
                    continue;

                InputFrame vehicleInput = default;

                if (IsMaster)
                {
                    int ownerId = vehicle.OwnerPlayerId;
                    if (_inputBuffer != null)
                        vehicleInput = _inputBuffer.ConsumeInput(ownerId, frame);
                }
                else
                {
                    VRCPlayerApi local = Networking.LocalPlayer;
                    int localId = local != null && local.IsValid() ? local.playerId : -1;

                    if (vehicle.OwnerPlayerId == localId)
                    {
                        vehicleInput = input;
                    }
                    else if (_inputPredictor != null)
                    {
                        vehicleInput = _inputPredictor.Predict(vehicle.OwnerPlayerId, frame, Time.time);
                    }
                }

                vehicle.ApplyInput(vehicleInput);
            }

            // Note: entity-specific simulation is game-defined and implemented by derived entities.
            // The world records state after user code has applied this tick's updates.
            _tempSnapshot.Frame = frame;
            _tempSnapshot.Timestamp = SimulationTime;
            _tempSnapshot.Capture(_entities, _entityCount);

            _history.Record(frame, _tempSnapshot, input);

            CurrentFrame = frame + 1;
        }

        // Network entry points (wired up in later phases).
        public void ReceiveInput(int playerId, byte[] data)
        {
            if (data == null || data.Length < 1)
                return;

            int frameCount = data[0];
            int offset = 1;

            for (int i = 0; i < frameCount; i++)
            {
                if (offset + InputFrame.NetworkSizeBytes > data.Length)
                    break;

                InputFrame input = InputFrame.ReadNetworkBytes(data, ref offset);

                // Track last-known input on all clients for remote prediction.
                if (_inputPredictor != null)
                    _inputPredictor.UpdateLastKnown(playerId, input, Time.time);

                // Master buffers inputs for authoritative consumption.
                if (IsMaster && _inputBuffer != null)
                    _inputBuffer.EnqueueInput(playerId, input);
            }
        }
        public void ReceiveState(byte[] data)
        {
            if (data == null || data.Length == 0 || _stateCompressor == null || _receivedSnapshot == null)
                return;

            _receivedSnapshot.Deserialize(data, _stateCompressor);

            // Only clients reconcile; master is authoritative.
            if (!IsMaster && _rollbackManager != null)
            {
                _rollbackManager.ProcessServerState(_receivedSnapshot.Frame, _receivedSnapshot);
            }
        }

        public void BroadcastState()
        {
            if (!IsMaster || _stateCompressor == null || _broadcastSnapshot == null)
                return;

            int maxEntities = Mathf.Clamp(MaxEntitiesPerStatePacket, 1, 8);

            _broadcastSnapshot.Frame = CurrentFrame - 1;
            _broadcastSnapshot.Timestamp = Time.time;

            // Build a capped snapshot to stay within ~200 bytes per sync packet.
            _broadcastSnapshot.EntityCount = 0;
            _broadcastSnapshot.Initialize(maxEntities);

            int writeIndex = 0;

            // Always include ball if present.
            int ballIndex = -1;
            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] != null && _entities[i].EntityType == EntityType.Ball)
                {
                    ballIndex = i;
                    break;
                }
            }

            if (ballIndex >= 0 && writeIndex < maxEntities)
            {
                _entities[ballIndex].SaveState(_broadcastSnapshot, writeIndex);
                writeIndex++;
            }

            // Round-robin the remaining entities.
            int maxOther = maxEntities - writeIndex;
            if (maxOther > 0 && _entityCount > 0)
            {
                int idx = _stateChunkCursor % _entityCount;
                int iterLimit = _entityCount;
                int added = 0;

                for (int iter = 0; iter < iterLimit && added < maxOther; iter++)
                {
                    var e = _entities[idx];
                    idx = (idx + 1) % _entityCount;

                    if (e == null)
                        continue;
                    if (ballIndex >= 0 && e == _entities[ballIndex])
                        continue;

                    e.SaveState(_broadcastSnapshot, writeIndex);
                    writeIndex++;
                    added++;
                }

                _stateChunkCursor = idx;
            }

            _broadcastSnapshot.EntityCount = writeIndex;

            _stateData = _broadcastSnapshot.Serialize(_stateCompressor);
            _stateSequence++;

            RequestSerialization();
        }

        public FrameHistory GetHistory() => _history;

        internal void SetCurrentFrameInternal(int frame)
        {
            CurrentFrame = frame;
        }

        public InputBuffer GetInputBuffer() => _inputBuffer;
        public InputPredictor GetInputPredictor() => _inputPredictor;

        public override void OnDeserialization()
        {
            // Sync updates come via this behaviour's synced fields.
            if (IsMaster)
                return;

            if (_stateSequence == _lastProcessedStateSequence)
                return;

            _lastProcessedStateSequence = _stateSequence;
            ReceiveState(_stateData);
        }

        private void EnsureEntityCapacity(int required)
        {
            if (_entities != null && _entities.Length >= required)
                return;

            int newSize = Mathf.Max(required, _entities == null ? 1 : _entities.Length * 2);
            var newArray = new NetPhysicsEntity[newSize];
            if (_entities != null && _entityCount > 0)
                System.Array.Copy(_entities, newArray, _entityCount);

            _entities = newArray;
        }

        private int FindNextFreeEntityId()
        {
            int id = 0;
            while (GetEntity(id) != null)
                id++;
            return id;
        }
    }
}
