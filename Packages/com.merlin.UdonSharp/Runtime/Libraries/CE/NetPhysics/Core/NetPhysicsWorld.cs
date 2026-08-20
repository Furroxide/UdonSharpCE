using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
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
        /// <summary>
        /// VRChat's payload ceiling for a Manual-sync behaviour. The eight-entity cap this used to
        /// carry was sized against the Continuous limit (~200 bytes), which a Manual behaviour is not
        /// subject to at all.
        /// </summary>
        public const int ManualSyncMaxBytes = 280496;

        [Header("Simulation")]
        public int TickRate = 60;
        public int MaxTicksPerFrame = 4;
        public bool AutoSimulate = true;

        [Header("History")]
        public int HistorySize = 128;
        public int MaxEntities = 64;

        [Header("Clock")]
        [Tooltip("Frames of drift from the shared server clock tolerated before the local frame counter is resynchronised and history dropped.")]
        public int ClockResyncThresholdFrames = 20;

        [Header("Sync (Owner)")]
        public float StateSendRate = 15f;
        [Tooltip("Upper bound on entities per state packet. The effective cap is also limited by StateBytesPerSecondBudget.")]
        public int MaxEntitiesPerStatePacket = 32;
        [Tooltip("Bytes per second this world may spend on state broadcasts. VRChat gives the whole instance roughly 11 KB/s, so this -- not the packet size limit -- is the real constraint.")]
        public float StateBytesPerSecondBudget = 4096f;

        /// <summary>
        /// True when the local client owns this object, and is therefore the only client whose writes
        /// to the synced state actually leave the machine.
        /// </summary>
        public bool IsAuthority => Networking.IsOwner(gameObject);

        public bool IsMaster => Networking.IsMaster;

        public int CurrentFrame => _currentFrame;
        public float SimulationTime => _clock != null ? _clock.SimulationTime : 0f;
        public int EntityCount => _entityCount;

        public NetPhysicsEntity[] Entities => _entities;

        private PhysicsClock _clock;
        private FrameHistory _history;
        private PhysicsSnapshot _tempSnapshot;
        private InputBuffer _inputBuffer;
        private InputPredictor _inputPredictor;
        private SyncPrioritizer _prioritizer;

        // Starts empty rather than at a fixed size: every client compiles in this default, and a block
        // of zero bytes is indistinguishable on arrival from a real packet describing frame 0.
        [UdonSynced] private byte[] _stateData = new byte[0];
        [UdonSynced] private int _stateSequence;

        private int _currentFrame;
        private int _lastProcessedStateSequence;
        private float _lastStateSendTime;

        // Consecutive failed serializations, driving the retry backoff in GetStateSendInterval.
        private int _stateSendFailures;
        private const int StateSendMaxBackoffDoublings = 5;
        private const float StateSendMaxBackoffSeconds = 2f;

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
            _prioritizer = new SyncPrioritizer();

            _currentFrame = GetServerFrame();

            ClaimAuthorityIfMaster();
        }

        private void FixedUpdate()
        {
            if (AutoSimulate)
                Simulate();

            if (!IsAuthority || StateSendRate <= 0f)
                return;

            // The rate gate is authoritative, including for retries. A retry used to bypass it entirely,
            // so one failed serialization made this run BroadcastState every FixedUpdate -- 50 Hz --
            // until one succeeded. A serialization failure in VRChat means this client is already over
            // its share of the instance's ~11 KB/s, so that turned congestion into a storm.
            if (Time.time - _lastStateSendTime < GetStateSendInterval())
                return;

            BroadcastState();
            _lastStateSendTime = Time.time;
        }

        /// <summary>
        /// Seconds to wait before the next state broadcast, backing off while serialization is failing.
        /// </summary>
        private float GetStateSendInterval()
        {
            float interval = 1f / StateSendRate;
            if (_stateSendFailures <= 0)
                return interval;

            // Each consecutive failure doubles the wait, so a client that cannot get a packet out backs
            // away from the congested link instead of leaning on it harder. The cap keeps a world that
            // recovers from being stuck waiting seconds for its first successful send.
            int doublings = _stateSendFailures < StateSendMaxBackoffDoublings ? _stateSendFailures : StateSendMaxBackoffDoublings;

            float backoff = interval;
            for (int i = 0; i < doublings; i++)
                backoff *= 2f;

            if (backoff > StateSendMaxBackoffSeconds)
                backoff = StateSendMaxBackoffSeconds;

            return backoff > interval ? backoff : interval;
        }

        /// <summary>
        /// The synced world state is written by whoever owns this object, not by whoever happens to be
        /// master. Keeping the two on the same client is what makes a master handover survive: a master
        /// that does not own this object has every write silently discarded, and the owner it is
        /// deferring to may have left the instance.
        /// </summary>
        private bool ClaimAuthorityIfMaster()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null || !local.IsValid())
                return false;

            if (Networking.IsOwner(local, gameObject))
                return true;

            if (!Networking.IsMaster)
                return false;

            Networking.SetOwner(local, gameObject);
            return Networking.IsOwner(local, gameObject);
        }

        public override void OnMasterTransferred(VRCPlayerApi newMaster)
        {
            if (newMaster != null && newMaster.isLocal)
                ClaimAuthorityIfMaster();
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return;

            // A new owner starts its own send cadence and its own round-robin position; inheriting the
            // previous owner's timer would either stall the first broadcast or burst several at once.
            _lastStateSendTime = Time.time;
            _stateChunkCursor = 0;
            _stateSendFailures = 0;

            // The client that just handed the world over has to start accepting packets again even
            // though the sequence counter continues from the value it wrote itself.
            if (!player.isLocal)
                _lastProcessedStateSequence = _stateSequence;
        }

        public override void OnPostSerialization(SerializationResult result)
        {
            // A dropped serialization used to be invisible: the sequence had already advanced, so that
            // frame of state was simply lost and every client reconciled against a snapshot it never
            // received. The retry is scheduled by GetStateSendInterval rather than run immediately --
            // VRChat only fails a serialization when this client is already over budget, so the retry
            // has to be the thing that eases off, not the thing that hammers.
            if (result.success)
            {
                _stateSendFailures = 0;
                return;
            }

            if (_stateSendFailures < StateSendMaxBackoffDoublings)
                _stateSendFailures++;
        }

        /// <summary>
        /// The frame number every client agrees on, derived from the instance-wide server clock.
        /// </summary>
        private int GetServerFrame()
        {
            int tickRate = Mathf.Max(1, TickRate);
            int milliseconds = Networking.GetServerTimeInMilliseconds();

            // Split rather than multiplying the whole millisecond count: milliseconds alone already
            // reaches two billion, so scaling it by the tick rate in one step overflows an int and
            // hands every client a different frame number for the same instant.
            return milliseconds / 1000 * tickRate + milliseconds % 1000 * tickRate / 1000;
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
                if (_entities[i] == null)
                    continue;
                if (_entities[i] == entity || _entities[i].EntityId == entity.EntityId)
                    return;
            }

            EnsureEntityCapacity(_entityCount + 1);

            // Insert sorted by EntityId for stable snapshot ordering.
            int insertIndex = _entityCount;
            while (insertIndex > 0 && _entities[insertIndex - 1] != null && _entities[insertIndex - 1].EntityId > entity.EntityId)
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

        /// <summary>
        /// Vehicles with a named driver.
        /// </summary>
        /// <remarks>
        /// Every driven vehicle runs its own <see cref="InputRecorder"/> publishing its own input, so
        /// this is the divisor for any per-vehicle share of the instance's bandwidth. A parked pool car
        /// has no driver and publishes nothing, which is why the count is of drivers and not of cars.
        /// </remarks>
        public int GetDrivenVehicleCount()
        {
            int count = 0;

            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] == null)
                    continue;

                EntityType entityType = _entities[i].EntityType;
                if (entityType != EntityType.LocalVehicle && entityType != EntityType.OtherVehicle)
                    continue;

                if (((NetVehicle)_entities[i]).OwnerPlayerId >= 0)
                    count++;
            }

            return count;
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

            // The frame counter used to be a purely local accumulator, so every client numbered the
            // same moment differently. Rollback then found every incoming packet outside its 128-entry
            // window, took the "too old" path, force-rewrote the clock and drifted straight back out:
            // a permanent snap-and-warn loop. Anchoring to the server clock is what breaks that cycle.
            int serverFrame = GetServerFrame();
            int drift = serverFrame - _currentFrame;
            int resyncThreshold = Mathf.Max(1, ClockResyncThresholdFrames);

            if (drift > resyncThreshold || drift < -resyncThreshold)
            {
                // A hitch or a late join leaves the counter too far out to walk back. History has to go
                // with it: the ring buffer indexes by frame number, so keeping it would leave it
                // claiming to hold frames it never recorded.
                _currentFrame = serverFrame;
                _history.Clear();
                _clock.Reset();
                return;
            }

            int ticks = _clock.ConsumeTicks(Time.fixedDeltaTime, MaxTicksPerFrame);

            // Converge on the shared clock a tick at a time rather than jumping, so frame numbers stay
            // contiguous and history stays usable.
            if (drift > 0 && ticks < MaxTicksPerFrame)
                ticks++;
            else if (drift < 0 && ticks > 0)
                ticks--;

            for (int i = 0; i < ticks; i++)
            {
                SimulateSingleTick(GetLocalInput());
            }
        }

        /// <summary>
        /// Simulates a single tick. Used for rollback resimulation.
        /// </summary>
        /// <param name="input">Input for the locally driven vehicle on this frame.</param>
        public void SimulateSingleTick(InputFrame input)
        {
            int frame = _currentFrame;
            if (input == null)
                input = InputFrame.Empty;

            VRCPlayerApi local = Networking.LocalPlayer;
            int localId = local != null && local.IsValid() ? local.playerId : -1;
            bool isAuthority = IsAuthority;

            // Apply per-entity simulation for this tick.
            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] == null)
                    continue;

                // Check entity type first - the 'as' cast doesn't perform runtime type checking in Udon
                EntityType entityType = _entities[i].EntityType;
                if (entityType != EntityType.LocalVehicle && entityType != EntityType.OtherVehicle)
                    continue;

                var vehicle = (NetVehicle)_entities[i];
                int ownerId = vehicle.OwnerPlayerId;

                InputFrame vehicleInput = InputFrame.Empty;

                if (localId >= 0 && ownerId == localId)
                {
                    // Our own car never waits on the buffer, on the authority client either: routing it
                    // through the jitter buffer would add the buffer depth as input latency to the one
                    // vehicle whose input is already local.
                    vehicleInput = input;
                }
                else if (isAuthority && _inputBuffer != null)
                {
                    vehicleInput = _inputBuffer.ConsumeInput(ownerId, frame);
                }
                else if (_inputPredictor != null)
                {
                    vehicleInput = _inputPredictor.Predict(ownerId, frame, Time.time);
                }

                vehicle.ApplyInput(vehicleInput);
            }

            // Note: entity-specific simulation is game-defined and implemented by derived entities.
            // The world records state after user code has applied this tick's updates.
            _tempSnapshot.Frame = frame;
            _tempSnapshot.Timestamp = SimulationTime;
            _tempSnapshot.Capture(_entities, _entityCount);

            _history.Record(frame, _tempSnapshot, input);

            _currentFrame = frame + 1;
        }

        /// <summary>
        /// Input for the vehicle this client drives, read from that vehicle's own recorder.
        /// A single shared recorder cannot work: every client sampled the same object, so a client that
        /// did not own it published nothing and read someone else's input.
        /// </summary>
        private InputFrame GetLocalInput()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null || !local.IsValid())
                return InputFrame.Empty;

            int localId = local.playerId;

            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] == null)
                    continue;

                EntityType entityType = _entities[i].EntityType;
                if (entityType != EntityType.LocalVehicle && entityType != EntityType.OtherVehicle)
                    continue;

                var vehicle = (NetVehicle)_entities[i];
                if (vehicle.OwnerPlayerId != localId)
                    continue;

                InputRecorder recorder = vehicle.Recorder;
                if (recorder == null)
                    continue;

                InputFrame frame = recorder.CurrentInput;
                return frame != null ? frame : InputFrame.Empty;
            }

            return InputFrame.Empty;
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

                // The authority buffers inputs for authoritative consumption.
                if (IsAuthority && _inputBuffer != null)
                    _inputBuffer.EnqueueInput(playerId, input);
            }
        }

        public void ReceiveState(byte[] data)
        {
            if (data == null || data.Length == 0 || _stateCompressor == null || _receivedSnapshot == null)
                return;

            // A rejected packet leaves the snapshot holding the previous state, so reconciling against
            // it would re-apply stale positions.
            if (!_receivedSnapshot.Deserialize(data, _stateCompressor))
                return;

            if (_receivedSnapshot.EntityCount <= 0)
                return;

            // Only non-authority clients reconcile; the owner is authoritative.
            if (!IsAuthority && _rollbackManager != null)
            {
                _rollbackManager.ProcessServerState(_receivedSnapshot.Frame, _receivedSnapshot);
            }
        }

        /// <summary>
        /// How many entities one state packet may carry.
        /// </summary>
        public int GetStatePacketEntityCap()
        {
            int cap = MaxEntitiesPerStatePacket > 0 ? MaxEntitiesPerStatePacket : 1;

            int byPacket = (ManualSyncMaxBytes - StateCompressor.HeaderBytes) / StateCompressor.BytesPerEntity;
            if (cap > byPacket)
                cap = byPacket;

            // The packet ceiling is not what actually binds here. VRChat gives the whole instance
            // roughly 11 KB/s, so at any realistic send rate the bandwidth budget runs out thousands of
            // entities before the 280 KB Manual payload limit does.
            if (StateSendRate > 0f && StateBytesPerSecondBudget > 0f)
            {
                float bytesPerPacket = StateBytesPerSecondBudget / StateSendRate;
                int byBudget = (int)((bytesPerPacket - StateCompressor.HeaderBytes) / StateCompressor.BytesPerEntity);
                if (cap > byBudget)
                    cap = byBudget;
            }

            return Mathf.Max(1, cap);
        }

        public void BroadcastState()
        {
            if (!IsAuthority || _stateCompressor == null || _broadcastSnapshot == null)
                return;

            int maxEntities = GetStatePacketEntityCap();

            _broadcastSnapshot.Frame = _currentFrame - 1;
            _broadcastSnapshot.Timestamp = Time.time;

            _broadcastSnapshot.EntityCount = 0;
            _broadcastSnapshot.Initialize(maxEntities);

            VRCPlayerApi local = Networking.LocalPlayer;
            int writeIndex = 0;

            // Fill the packet in descending sync priority so that, when the arena holds more bodies
            // than one packet can carry, the ball and the cars people are actually near go out first.
            // The round-robin cursor is kept as the scan origin so equal-priority entities rotate
            // through packets instead of the low-index ones winning every time.
            if (_entityCount > 0)
            {
                int origin = _stateChunkCursor % _entityCount;

                while (writeIndex < maxEntities)
                {
                    int bestIndex = -1;
                    float bestPriority = 0f;

                    for (int scan = 0; scan < _entityCount; scan++)
                    {
                        int i = (origin + scan) % _entityCount;

                        var candidate = _entities[i];
                        if (candidate == null)
                            continue;
                        if (IsAlreadyInPacket(candidate.EntityId, writeIndex))
                            continue;

                        float priority = _prioritizer != null ? _prioritizer.CalculatePriority(candidate, local) : 1f;
                        if (bestIndex >= 0 && priority <= bestPriority)
                            continue;

                        bestPriority = priority;
                        bestIndex = i;
                    }

                    if (bestIndex < 0)
                        break;

                    _entities[bestIndex].SaveState(_broadcastSnapshot, writeIndex);
                    writeIndex++;
                }

                _stateChunkCursor = (origin + 1) % _entityCount;
            }

            _broadcastSnapshot.EntityCount = writeIndex;

            if (writeIndex <= 0)
            {
                // No packet means nothing can fail, so an empty world must not sit backed off forever
                // on the strength of a failure that predates it.
                _stateSendFailures = 0;
                return;
            }

            _stateData = _broadcastSnapshot.Serialize(_stateCompressor);
            _stateSequence++;

            RequestSerialization();
        }

        private bool IsAlreadyInPacket(int entityId, int writeCount)
        {
            for (int i = 0; i < writeCount; i++)
            {
                if (_broadcastSnapshot.EntityIds[i] == entityId)
                    return true;
            }

            return false;
        }

        public FrameHistory GetHistory() => _history;

        /// <summary>
        /// Moves the frame cursor for rollback resimulation only. The cursor is otherwise owned by the
        /// server clock in <see cref="Simulate"/>: rewriting it to chase an incoming packet is exactly
        /// what used to make this world drift out of the history window and snap on every packet.
        /// </summary>
        internal void SetCurrentFrameInternal(int frame)
        {
            _currentFrame = frame;
        }

        public InputBuffer GetInputBuffer() => _inputBuffer;
        public InputPredictor GetInputPredictor() => _inputPredictor;

        public override void OnDeserialization()
        {
            // Sync updates come via this behaviour's synced fields.
            if (IsAuthority)
                return;

            // Sequence 0 is the value every client compiles in, so an untouched default must not be
            // mistaken for a packet: it used to be processed exactly once, feeding an empty snapshot
            // stamped frame 0 into rollback and triggering a snap on join.
            if (_stateSequence == 0 || _stateSequence == _lastProcessedStateSequence)
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
