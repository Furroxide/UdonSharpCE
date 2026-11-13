using JetBrains.Annotations;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Reconciles this client's prediction with the state the authority broadcasts.
    /// </summary>
    /// <remarks>
    /// The name is historical: nothing here rewinds or replays. It used to restore the authority's
    /// state and call SimulateSingleTick once per frame since, on the understanding that this
    /// resimulated the gap. Udon cannot step PhysX -- Physics.Simulate does nothing while automatic
    /// simulation is on, and there is no way to turn that off -- so the replay moved no bodies. What it
    /// actually did was leave every corrected entity at the pose the authority had held a network
    /// delay earlier, stack one physics step's worth of force per replayed frame into a single step,
    /// and fire jump and dodge impulses a second time.
    ///
    /// What it does instead is delta correction. The error is measured where both sides can be
    /// compared like for like -- the authority's state against this client's own recorded prediction
    /// for the same frame -- and that difference is applied to the entity as it stands now, and to the
    /// recorded frames from that one on so the next packet does not measure the same error again. A
    /// good prediction therefore costs nothing however fast the entity moves, and a bad one moves the
    /// entity by the size of the misprediction rather than by the distance it travelled in transit.
    /// </remarks>
    [PublicAPI]
    public class RollbackManager
    {
        private NetPhysicsWorld _world;
        private FrameHistory _history;

        /// <summary>
        /// Position error in metres tolerated for an entity at rest. See <see cref="TimingSlackSteps"/>
        /// for how the allowance grows with speed.
        /// </summary>
        public float PositionThreshold = 0.05f;

        /// <summary>
        /// Furthest, in frames, a received state is extrapolated forward when this client holds no
        /// prediction to compare it against. (It used to cap how many frames were replayed.)
        /// </summary>
        public int MaxRollbackFrames = 30;

        /// <summary>
        /// Physics steps of travel by which two clients in perfect agreement may still differ at the
        /// same frame number. Frame numbers come from the wall clock but poses only change once per
        /// physics step, so "frame N" is sampled up to a step early or late on each side. Without this
        /// allowance anything faster than a few metres per second read as mispredicted on every packet.
        /// </summary>
        public float TimingSlackSteps = 1.5f;

        /// <summary>
        /// True only while <see cref="Reconcile"/> is applying corrections. Processing is synchronous,
        /// so it always reads false from anywhere else.
        /// </summary>
        public bool IsRollingBack { get; private set; }

        /// <summary>
        /// Frame the most recent correction was measured at.
        /// </summary>
        public int RollbackFrame { get; private set; }

        /// <summary>
        /// Entities corrected by the most recent <see cref="Reconcile"/>.
        /// </summary>
        public int LastCorrectionCount { get; private set; }

        // Result of the most recent CalculateCorrection, valid when it returned true.
        public Vector3 PositionDelta;
        public Quaternion RotationDelta;
        public Vector3 VelocityDelta;
        public Vector3 AngularVelocityDelta;

        public void Initialize(NetPhysicsWorld world, FrameHistory history)
        {
            _world = world;
            _history = history;
        }

        /// <summary>
        /// Processes a state received from the authority and corrects every entity in it whose
        /// prediction was off. Synchronous; completes within the call.
        /// </summary>
        public void ProcessServerState(int serverFrame, PhysicsSnapshot serverState)
        {
            if (_world == null)
                return;

            VRCPlayerApi local = Networking.LocalPlayer;
            int localPlayerId = local != null && local.IsValid() ? local.playerId : -1;

            Reconcile(serverFrame, serverState, _world.Entities, _world.EntityCount, _world.CurrentFrame, _world.TickRate, localPlayerId);
        }

        /// <summary>
        /// As <see cref="ProcessServerState"/>, for a caller that already holds the registry and the
        /// clock. The world calls this one, so that reconciling does not have to call back into the
        /// world while the world is in the middle of handling the packet.
        /// </summary>
        /// <param name="currentFrame">The frame the world will simulate next.</param>
        /// <param name="localPlayerId">This client's player id, or -1. A vehicle naming it as its driver is the one predicted ahead of the authority.</param>
        public void Reconcile(int serverFrame, PhysicsSnapshot serverState, NetPhysicsEntity[] entities, int entityCount, int currentFrame, int tickRate, int localPlayerId)
        {
            LastCorrectionCount = 0;

            if (_history == null || serverState == null || entities == null)
                return;

            float tickDelta = 1f / Mathf.Max(1, tickRate);
            float slackSeconds = TimingSlackSteps * Time.fixedDeltaTime;
            int framesOld = (currentFrame - 1) - serverFrame;

            IsRollingBack = true;

            int count = serverState.EntityCount;
            for (int i = 0; i < count; i++)
            {
                int entityId = serverState.EntityIds[i];
                NetPhysicsEntity entity = FindEntity(entities, entityCount, entityId);
                if (entity == null)
                    continue;

                int compareFrame = serverFrame;
                float minimumError = 0f;

                // The car this client drives is the one entity it predicts ahead of the authority: its
                // input is applied here the moment it is sampled, and by the authority only once it has
                // crossed the network and the jitter buffer. Compared at the same frame number the two
                // have applied different amounts of input, so the car read as mispredicted whenever the
                // driver touched the controls and was pulled back by the input delay on every packet.
                // The authority says how far behind the input it had applied was stamped; comparing
                // against this client's prediction for that frame lines the two back up.
                int inputLag = serverState.InputLagFrames[i];
                if (inputLag != 0 && IsDrivenBy(entity, localPlayerId))
                {
                    if (inputLag > 0)
                    {
                        compareFrame = serverFrame - inputLag;
                    }
                    else
                    {
                        // No usable figure, so there is nothing to line the car up with. Leave the
                        // driver in charge of it unless it is far enough out to need the jump anyway.
                        minimumError = entity.CorrectionSnapDistance;
                    }
                }

                PhysicsSnapshot localState = _history.GetState(compareFrame);
                int localIndex = localState != null ? localState.IndexOfEntity(entityId) : -1;

                bool needsCorrection;
                int historyFromFrame;

                if (localIndex >= 0)
                {
                    needsCorrection = CalculateCorrection(serverState, i, localState, localIndex, slackSeconds);
                    historyFromFrame = compareFrame;
                }
                else
                {
                    // Nothing recorded for that frame: this client has only just joined or resynchronised
                    // its clock, or the entity registered after it. The received pose is still the
                    // network delay old, so it is carried forward along its own velocity to roughly
                    // where the authority has the entity now, and measured against the present.
                    if (compareFrame != serverFrame && minimumError < entity.CorrectionSnapDistance)
                        minimumError = entity.CorrectionSnapDistance;

                    needsCorrection = CalculatePresentCorrection(serverState, i, entity, framesOld, tickDelta, slackSeconds);
                    historyFromFrame = int.MinValue;
                }

                if (!needsCorrection || PositionDelta.magnitude <= minimumError)
                    continue;

                entity.ApplyCorrectionDelta(PositionDelta, RotationDelta, VelocityDelta, AngularVelocityDelta);
                _history.OffsetEntity(entityId, historyFromFrame, PositionDelta, RotationDelta, VelocityDelta, AngularVelocityDelta);

                RollbackFrame = compareFrame;
                LastCorrectionCount++;
            }

            IsRollingBack = false;
        }

        /// <summary>
        /// Measures one entity's prediction error: the authority's state minus this client's recorded
        /// state for the same frame. Returns true, with the error in <see cref="PositionDelta"/>,
        /// <see cref="RotationDelta"/>, <see cref="VelocityDelta"/> and
        /// <see cref="AngularVelocityDelta"/>, when the position error is past the tolerance.
        /// </summary>
        public bool CalculateCorrection(PhysicsSnapshot serverState, int serverIndex, PhysicsSnapshot localState, int localIndex, float slackSeconds)
        {
            if (serverState == null || localState == null)
                return false;

            if (serverIndex < 0 || serverIndex >= serverState.EntityCount || localIndex < 0 || localIndex >= localState.EntityCount)
                return false;

            Vector3 serverVelocity = serverState.Velocities[serverIndex];
            Vector3 localVelocity = localState.Velocities[localIndex];

            PositionDelta = serverState.Positions[serverIndex] - localState.Positions[localIndex];

            float tolerance = CalculateTolerance(PositionThreshold, serverVelocity.magnitude, localVelocity.magnitude, slackSeconds);
            if (PositionDelta.magnitude <= tolerance)
                return false;

            RotationDelta = serverState.Rotations[serverIndex] * Quaternion.Inverse(localState.Rotations[localIndex]);
            VelocityDelta = serverVelocity - localVelocity;
            AngularVelocityDelta = serverState.AngularVelocities[serverIndex] - localState.AngularVelocities[localIndex];
            return true;
        }

        /// <summary>
        /// Position error tolerated before an entity is corrected: the base threshold plus the distance
        /// the faster of the two states covers in <paramref name="slackSeconds"/>.
        /// </summary>
        public static float CalculateTolerance(float baseThreshold, float serverSpeed, float localSpeed, float slackSeconds)
        {
            float speed = serverSpeed > localSpeed ? serverSpeed : localSpeed;
            if (slackSeconds < 0f)
                slackSeconds = 0f;

            return baseThreshold + speed * slackSeconds;
        }

        /// <summary>
        /// Carries a received position forward along its velocity by the age of the state, capped at
        /// <paramref name="maxFrames"/> so a stale packet cannot throw an entity across the arena.
        /// </summary>
        public static Vector3 ExtrapolatePosition(Vector3 position, Vector3 velocity, int framesOld, int maxFrames, float tickDelta)
        {
            int frames = framesOld;
            if (frames > maxFrames)
                frames = maxFrames;
            if (frames < 0)
                frames = 0;

            return position + velocity * (frames * tickDelta);
        }

        /// <summary>
        /// The no-history counterpart of <see cref="CalculateCorrection"/>: measures the entity as it
        /// stands now against the received state carried forward to the present.
        /// </summary>
        private bool CalculatePresentCorrection(PhysicsSnapshot serverState, int serverIndex, NetPhysicsEntity entity, int framesOld, float tickDelta, float slackSeconds)
        {
            Vector3 serverVelocity = serverState.Velocities[serverIndex];
            Vector3 localVelocity = entity.Velocity;

            Vector3 presentPosition = ExtrapolatePosition(serverState.Positions[serverIndex], serverVelocity, framesOld, MaxRollbackFrames, tickDelta);
            PositionDelta = presentPosition - entity.CorrectedPosition;

            float tolerance = CalculateTolerance(PositionThreshold, serverVelocity.magnitude, localVelocity.magnitude, slackSeconds);
            if (PositionDelta.magnitude <= tolerance)
                return false;

            RotationDelta = serverState.Rotations[serverIndex] * Quaternion.Inverse(entity.CorrectedRotation);
            VelocityDelta = serverVelocity - localVelocity;
            AngularVelocityDelta = serverState.AngularVelocities[serverIndex] - entity.AngularVelocity;
            return true;
        }

        /// <summary>
        /// True when <paramref name="entity"/> is a vehicle whose driver is <paramref name="playerId"/>.
        /// </summary>
        private static bool IsDrivenBy(NetPhysicsEntity entity, int playerId)
        {
            if (playerId < 0)
                return false;

            // The type is checked before the cast: Udon's cast does not check it.
            EntityType entityType = entity.EntityType;
            if (entityType != EntityType.LocalVehicle && entityType != EntityType.OtherVehicle)
                return false;

            return ((NetVehicle)entity).OwnerPlayerId == playerId;
        }

        private static NetPhysicsEntity FindEntity(NetPhysicsEntity[] entities, int entityCount, int entityId)
        {
            if (entityId < 0)
                return null;

            for (int i = 0; i < entityCount; i++)
            {
                NetPhysicsEntity entity = entities[i];
                if (entity != null && entity.EntityId == entityId)
                    return entity;
            }

            return null;
        }
    }
}
