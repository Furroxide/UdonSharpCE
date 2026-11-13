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

        private StateCompressor _stateCompressor;
        private PhysicsSnapshot _receivedSnapshot;
        private PhysicsSnapshot _broadcastSnapshot;
        private RollbackManager _rollbackManager;

        // The box state packets quantize positions into, measured from this object's own position. It
        // used to be fixed in world space around the origin, and anything outside it is clamped onto
        // its surface: the shipped arena sits 121 m down the z axis, so most of it arrived pinned to the
        // z = -100 face on every client but the authority. Every client derives the same box because
        // every client has this object in the same place.
        //
        // The defaults reach 100 m either side of this object and from 10 m below it to 150 m above. The
        // height is what a car can climb: the jump and double jump followed by a full two-second tank
        // pointed straight up -- 5000 N on 180 kg against gravity -- peaks about 50 m up with the shipped
        // car's drag and about 130 m up with none, and the shipped arena has no ceiling. A 50 m box
        // clamped that climb onto its lid on every client but the authority. Each axis is quantized to
        // 65,536 steps across the box, so the defaults still place a position within about 1.5 mm per
        // axis, 2.5 mm overall.
        [Tooltip("Low corner of the box state packets encode positions in, relative to this object. A position outside the box is clamped onto it.")]
        public Vector3 StateBoundsMin = new Vector3(-100f, -10f, -100f);
        [Tooltip("High corner of the box state packets encode positions in, relative to this object. A position outside the box is clamped onto it.")]
        public Vector3 StateBoundsMax = new Vector3(100f, 150f, 100f);

        // Per-entity send scheduling for BroadcastState, parallel to _entities. See
        // SyncPrioritizer.SelectForPacket.
        private float[] _sendPriorities;
        private float[] _sendDebt;
        private int[] _sendSelection;
        private NetPhysicsEntity[] _entities;
        private int _entityCount;

        // Set when an entity registers or leaves; the automatic ids are worked out again before they are
        // next used. See EnsureEntityIds.
        private bool _entityIdsDirty;

        private void Start()
        {
            EnsureEntityCapacity(Mathf.Max(1, MaxEntities));
            _clock = new PhysicsClock(TickRate);
            _history = new FrameHistory(Mathf.Max(1, HistorySize), _entities.Length);
            _tempSnapshot = new PhysicsSnapshot(_entities.Length);
            _inputBuffer = new InputBuffer();
            _inputPredictor = new InputPredictor();

            _stateCompressor = new StateCompressor();
            _stateCompressor.WorldMin = transform.position + StateBoundsMin;
            _stateCompressor.WorldMax = transform.position + StateBoundsMax;
            _receivedSnapshot = new PhysicsSnapshot(_entities.Length);
            _broadcastSnapshot = new PhysicsSnapshot(_entities.Length);
            _rollbackManager = new RollbackManager();
            _rollbackManager.Initialize(this, _history);
            _prioritizer = new SyncPrioritizer();

            _currentFrame = GetServerFrame();

            // Entities that registered before this ran have ids from here on. Any that register later
            // are folded in, and everything renumbered to match, before ids are next used.
            EnsureEntityIds();

            ClaimAuthorityIfMaster();
        }

        private void FixedUpdate()
        {
            if (AutoSimulate)
            {
                Simulate();
                ApplyVehicleForces();
            }

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

            // A new owner starts its own send cadence; inheriting the previous owner's timer would
            // either stall the first broadcast or burst several at once.
            _lastStateSendTime = Time.time;
            _stateSendFailures = 0;

            // The client that just handed the world over has to start accepting packets again even
            // though the sequence counter continues from the value it wrote itself.
            if (!player.isLocal)
            {
                _lastProcessedStateSequence = _stateSequence;
                return;
            }

            // This client's bodies are now the state everybody else is corrected toward. Whatever
            // correction they were still easing in describes the previous owner's simulation, and
            // the next broadcast would otherwise ship it: an entity records the pose it is being
            // corrected toward, not the one its Rigidbody is at.
            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] != null)
                    _entities[i].ClearCorrection();
            }
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            // The input tables keep a departed player's slot until told otherwise, and a full table
            // turns new drivers away rather than evict anybody. See InputBuffer.
            if (player == null) return;
            int playerId = player.playerId;
            if (_inputBuffer != null) _inputBuffer.RemovePlayer(playerId);
            if (_inputPredictor != null) _inputPredictor.RemovePlayer(playerId);
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

        /// <summary>
        /// Adds an entity to the simulation. An entity whose id is not pinned is numbered the next time
        /// ids are needed; see NetPhysicsEntity's _entityId for the rule.
        /// </summary>
        public void RegisterEntity(NetPhysicsEntity entity)
        {
            if (entity == null)
                return;

            bool pinned = entity.HasExplicitEntityId;

            for (int i = 0; i < _entityCount; i++)
            {
                NetPhysicsEntity registered = _entities[i];
                if (registered == null)
                    continue;
                if (registered == entity)
                    return;

                // Two entities pinned to one id cannot both be addressed; the second is left out.
                if (pinned && registered.HasExplicitEntityId && registered.EntityId == entity.EntityId)
                    return;
            }

            EnsureEntityCapacity(_entityCount + 1);

            _entities[_entityCount] = entity;
            _sendDebt[_entityCount] = 0f;
            _entityCount++;

            // Numbered as a batch rather than here: everything that registers in the same frame -- at
            // load, every entity in the scene -- then costs one pass, not one pass each.
            _entityIdsDirty = true;
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
                    _sendDebt[j] = _sendDebt[j + 1];
                }

                _entities[_entityCount - 1] = null;
                _sendDebt[_entityCount - 1] = 0f;
                _entityCount--;

                // The automatic ids are a function of who is registered, so they are worked out again
                // without this entity: a client that joins after it left never saw it at all.
                _entityIdsDirty = true;
                return;
            }
        }

        /// <summary>
        /// Numbers every entity whose id is not pinned, if anything has registered or left since the
        /// last time. See NetPhysicsEntity's _entityId for the rule.
        /// </summary>
        private void EnsureEntityIds()
        {
            if (!_entityIdsDirty)
                return;

            _entityIdsDirty = false;

            int count = _entityCount;
            var automatic = new NetPhysicsEntity[count];
            var pinnedIds = new int[count];
            int automaticCount = 0;
            int pinnedCount = 0;

            for (int i = 0; i < count; i++)
            {
                NetPhysicsEntity entity = _entities[i];
                if (entity == null)
                    continue;

                if (entity.HasExplicitEntityId)
                {
                    pinnedIds[pinnedCount] = entity.EntityId;
                    pinnedCount++;
                    continue;
                }

                // Kept in hierarchy order as they are collected.
                Transform place = entity.transform;
                int insert = automaticCount;
                while (insert > 0 && CompareHierarchyOrder(automatic[insert - 1].transform, place) > 0)
                {
                    automatic[insert] = automatic[insert - 1];
                    insert--;
                }

                automatic[insert] = entity;
                automaticCount++;
            }

            bool renumbered = false;
            int next = 0;
            for (int k = 0; k < automaticCount; k++)
            {
                while (ContainsId(pinnedIds, pinnedCount, next))
                    next++;

                NetPhysicsEntity entity = automatic[k];
                if (entity.EntityId != next)
                {
                    entity.SetEntityIdInternal(next);
                    renumbered = true;
                }

                next++;
            }

            SortEntitiesById();

            // Recorded frames name entities by id, so once an id has moved they describe the wrong
            // bodies. Any packet in flight is measured against an empty history instead.
            if (renumbered && _history != null)
                _history.Clear();
        }

        /// <summary>
        /// Negative when <paramref name="a"/> comes first in the scene hierarchy -- by sibling index from
        /// the scene root down, an ancestor before its descendants -- positive when <paramref name="b"/>
        /// does, zero for the same transform.
        /// </summary>
        private static int CompareHierarchyOrder(Transform a, Transform b)
        {
            if (a == b)
                return 0;

            int depthA = GetHierarchyDepth(a);
            int depthB = GetHierarchyDepth(b);

            // Lift the deeper one to the other's depth; landing on the other means it is an ancestor.
            Transform x = a;
            Transform y = b;
            for (int i = depthA; i > depthB; i--)
                x = x.parent;
            for (int i = depthB; i > depthA; i--)
                y = y.parent;

            if (x == y)
                return depthA < depthB ? -1 : 1;

            // Climb together until the two are siblings; their sibling indices decide. Two scene roots
            // count as siblings, ordered by their index among the scene's root objects.
            while (x.parent != y.parent)
            {
                x = x.parent;
                y = y.parent;
            }

            return x.GetSiblingIndex() < y.GetSiblingIndex() ? -1 : 1;
        }

        private static int GetHierarchyDepth(Transform transformInScene)
        {
            int depth = 0;
            Transform current = transformInScene;
            while (current != null)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private static bool ContainsId(int[] ids, int count, int id)
        {
            for (int i = 0; i < count; i++)
            {
                if (ids[i] == id)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Puts the registry back in id order, which snapshots follow.
        /// </summary>
        private void SortEntitiesById()
        {
            int count = _entityCount;
            var ids = new int[count];
            for (int i = 0; i < count; i++)
                ids[i] = _entities[i] != null ? _entities[i].EntityId : int.MaxValue;

            for (int i = 1; i < count; i++)
            {
                NetPhysicsEntity entity = _entities[i];
                float debt = _sendDebt[i];
                int id = ids[i];

                int j = i;
                while (j > 0 && ids[j - 1] > id)
                {
                    _entities[j] = _entities[j - 1];
                    _sendDebt[j] = _sendDebt[j - 1];
                    ids[j] = ids[j - 1];
                    j--;
                }

                _entities[j] = entity;
                _sendDebt[j] = debt;
                ids[j] = id;
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
            EnsureEntityIds();

            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i] != null && _entities[i].EntityId == entityId)
                    return _entities[i];
            }

            return null;
        }

        /// <summary>
        /// Runs simulation ticks. Manual FixedUpdate callers must call ApplyVehicleForces afterwards,
        /// once per physics step, including steps with no simulation tick.
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
        /// Simulates one logical tick at the current frame and records it in history. Per-tick vehicle
        /// logic only: the continuous forces belong to the physics step, see <see cref="ApplyVehicleForces"/>.
        /// </summary>
        /// <param name="input">Input for the locally driven vehicle on this frame.</param>
        public void SimulateSingleTick(InputFrame input)
        {
            // The recorded frame names entities by id.
            EnsureEntityIds();

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

                vehicle.TickInput(vehicleInput);
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
                // The packet names entities by id.
                EnsureEntityIds();

                VRCPlayerApi local = Networking.LocalPlayer;
                int localPlayerId = local != null && local.IsValid() ? local.playerId : -1;

                _rollbackManager.Reconcile(_receivedSnapshot.Frame, _receivedSnapshot, _entities, _entityCount, _currentFrame, TickRate, localPlayerId);
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

            // The packet names entities by id.
            EnsureEntityIds();

            int maxEntities = GetStatePacketEntityCap();

            _broadcastSnapshot.Frame = _currentFrame - 1;
            _broadcastSnapshot.Timestamp = Time.time;

            _broadcastSnapshot.EntityCount = 0;
            _broadcastSnapshot.Initialize(maxEntities);

            VRCPlayerApi local = Networking.LocalPlayer;
            int writeIndex = 0;

            for (int i = 0; i < _entityCount; i++)
                _sendPriorities[i] = _entities[i] != null ? _prioritizer.CalculatePriority(_entities[i], local) : -1f;

            writeIndex = _prioritizer.SelectForPacket(_sendPriorities, _sendDebt, _entityCount, maxEntities, _sendSelection);
            for (int n = 0; n < writeIndex; n++)
            {
                NetPhysicsEntity entity = _entities[_sendSelection[n]];
                entity.SaveState(_broadcastSnapshot, n);
                EntityType type = entity.EntityType;
                if (type == EntityType.LocalVehicle || type == EntityType.OtherVehicle)
                {
                    int driver = ((NetVehicle)entity).OwnerPlayerId;
                    if (driver >= 0 && (local == null || driver != local.playerId))
                        _broadcastSnapshot.InputLagFrames[n] = _inputBuffer != null ? _inputBuffer.GetInputLag(driver) : PhysicsSnapshot.UnknownInputLag;
                }
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

        /// <summary>
        /// Applies every vehicle's continuous forces for one physics step, from the input of its most
        /// recent tick. Called once per FixedUpdate after <see cref="Simulate"/>, whether or not that
        /// FixedUpdate ran a tick. See <see cref="NetVehicle.TickInput"/> for why the two are split.
        /// </summary>
        public void ApplyVehicleForces()
        {
            for (int i = 0; i < _entityCount; i++)
            {
                NetPhysicsEntity entity = _entities[i];
                if (entity == null) continue;
                EntityType type = entity.EntityType;
                if (type == EntityType.LocalVehicle || type == EntityType.OtherVehicle)
                    ((NetVehicle)entity).ApplyForces();
            }
        }

        public FrameHistory GetHistory() => _history;

        /// <summary>
        /// Moves the frame cursor. Nothing in this library calls it: the cursor is owned by the server
        /// clock in <see cref="Simulate"/>, and rewriting it to chase an incoming packet is exactly what
        /// used to make this world drift out of the history window and snap on every packet.
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
            if (_entities != null && _entities.Length >= required && _sendDebt != null)
                return;

            int newSize = Mathf.Max(required, _entities == null ? 1 : _entities.Length * 2);
            var newArray = new NetPhysicsEntity[newSize];
            var debt = new float[newSize];
            if (_entities != null && _entityCount > 0)
                System.Array.Copy(_entities, newArray, _entityCount);
            if (_sendDebt != null)
                System.Array.Copy(_sendDebt, debt, Mathf.Min(_sendDebt.Length, newSize));
            _entities = newArray;
            _sendDebt = debt;
            _sendPriorities = new float[newSize];
            _sendSelection = new int[newSize];
        }
    }
}
