using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Base class for any network-simulated physics object.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public abstract class NetPhysicsEntity : UdonSharpBehaviour
    {
        [Header("NetPhysics")]
        public NetPhysicsWorld World;

        // How state packets name this entity, so every client has to give it the same id. Set it to pin
        // the id by hand; leave it at -1 and the world numbers the entity itself.
        //
        // Automatic ids follow the scene hierarchy: entities are ordered by sibling index from the scene
        // root down, an ancestor before its descendants, and numbered from 0 in that order, stepping over
        // every id an entity has pinned. The world renumbers whenever an entity registers or leaves, so
        // the result depends only on which entities are registered, never on the order their Start ran
        // in. It used to depend on exactly that -- each entity took the lowest free id as it registered --
        // so two clients that started the ball and a car in a different order swapped their ids and
        // applied each other's packets to the wrong bodies. An entity sharing a GameObject with another,
        // or one that is reparented or reordered at runtime, needs a pinned id.
        [SerializeField] private int _entityId = -1;

        // The id the world assigned while _entityId is -1; -1 until it has.
        private int _assignedEntityId = -1;
        // Nothing in this library reads this: it was the interest manager's input and there is no
        // interest manager. It stays so existing scenes and prefabs keep their serialized value.
        [SerializeField] private bool _alwaysRelevant;
        [SerializeField] private Rigidbody _rb;

        [Header("Correction")]
        [Tooltip("Correction in metres above which the entity is moved at once instead of eased in.")]
        public float CorrectionSnapDistance = 2f;
        [Tooltip("Fraction of the remaining correction moved onto the body per physics step while easing it in. 1 moves it at once.")]
        [Range(0.01f, 1f)] public float CorrectionBlendRate = 0.2f;

        private CorrectionBlender _correctionBlender;

        protected Rigidbody RigidbodyComponent => _rb;

        /// <summary>
        /// The id state packets use for this entity: the pinned one, or the one the world assigned.
        /// -1 until the world has numbered an entity left to it, which it does by the time it next
        /// simulates, sends or receives state, looks an entity up, or starts.
        /// </summary>
        public int EntityId => _entityId >= 0 ? _entityId : _assignedEntityId;

        /// <summary>
        /// True when the id was set by hand rather than left for the world to assign.
        /// </summary>
        public bool HasExplicitEntityId => _entityId >= 0;
        public virtual EntityType EntityType => EntityType.Unknown;
        public bool AlwaysRelevant => _alwaysRelevant;

        public float LastInteractionTime { get; protected set; }

        /// <summary>
        /// Where the body is. Assigning is a teleport: any position correction still being eased in was
        /// measured against the place the body is being moved away from, so it is dropped rather than
        /// left to drag the body back toward it.
        /// </summary>
        public virtual Vector3 Position
        {
            get => _rb != null ? _rb.position : transform.position;
            set
            {
                if (_correctionBlender != null)
                    _correctionBlender.ClearPosition();

                if (_rb != null)
                    _rb.position = value;
                else
                    transform.position = value;
            }
        }

        /// <summary>
        /// How the body is oriented. Assigning is a teleport, as for <see cref="Position"/>.
        /// </summary>
        public virtual Quaternion Rotation
        {
            get => _rb != null ? _rb.rotation : transform.rotation;
            set
            {
                if (_correctionBlender != null)
                    _correctionBlender.ClearRotation();

                if (_rb != null)
                    _rb.rotation = value;
                else
                    transform.rotation = value;
            }
        }

        public virtual Vector3 Velocity
        {
            get => _rb != null ? _rb.velocity : Vector3.zero;
            set
            {
                if (_rb != null)
                    _rb.velocity = value;
            }
        }

        public virtual Vector3 AngularVelocity
        {
            get => _rb != null ? _rb.angularVelocity : Vector3.zero;
            set
            {
                if (_rb != null)
                    _rb.angularVelocity = value;
            }
        }

        /// <summary>
        /// True while part of a network correction has still to be moved onto the body.
        /// </summary>
        public bool HasPendingCorrection => _correctionBlender != null && _correctionBlender.HasOffset;

        /// <summary>
        /// Position correction accepted but not yet moved onto the body.
        /// </summary>
        public Vector3 PendingCorrectionPosition => _correctionBlender != null ? _correctionBlender.PendingPosition : Vector3.zero;

        /// <summary>
        /// Rotation correction accepted but not yet moved onto the body.
        /// </summary>
        public Quaternion PendingCorrectionRotation => _correctionBlender != null ? _correctionBlender.PendingRotation : Quaternion.identity;

        /// <summary>
        /// Where the body will be once the correction being eased in has finished. This, not
        /// <see cref="Position"/>, is what prediction history records and what the next correction is
        /// measured against: otherwise the part of a correction still on its way would be measured as
        /// fresh error by the next packet and applied a second time.
        /// </summary>
        public Vector3 CorrectedPosition => Position + PendingCorrectionPosition;

        /// <summary>
        /// Rotation counterpart of <see cref="CorrectedPosition"/>.
        /// </summary>
        public Quaternion CorrectedRotation => PendingCorrectionRotation * Rotation;

        protected virtual void Reset()
        {
            _rb = GetComponent<Rigidbody>();
        }

        protected virtual void Start()
        {
            if (_rb == null)
                _rb = GetComponent<Rigidbody>();

            if (World != null)
                World.RegisterEntity(this);
        }

        protected virtual void OnDestroy()
        {
            if (World != null)
                World.UnregisterEntity(this);
        }

        /// <summary>
        /// Drives the correction blend. Derived entities that need their own FixedUpdate must call
        /// base.FixedUpdate(), otherwise a started correction is never finished and the entity sits
        /// permanently short of the authoritative position.
        /// </summary>
        protected virtual void FixedUpdate()
        {
            if (_correctionBlender == null || !_correctionBlender.HasOffset)
                return;

            StepCorrection(World != null && World.IsAuthority);
        }

        /// <summary>
        /// Advances the correction blend by one physics step.
        /// </summary>
        private void StepCorrection(bool isAuthority)
        {
            if (_correctionBlender == null)
                return;

            // The authority is never corrected, so whatever it is still easing in was accepted while
            // some other client was the authority. Finishing it would move the one body everybody else
            // is told to follow toward a state its previous owner described, and nothing would ever
            // arrive to say stop.
            if (isAuthority || _rb == null)
            {
                _correctionBlender.Clear();
                return;
            }

            _correctionBlender.ApplyBlend(_rb);
        }

        /// <summary>
        /// Drops any correction still being eased in, leaving the body where it is.
        /// </summary>
        public void ClearCorrection()
        {
            if (_correctionBlender != null)
                _correctionBlender.Clear();
        }

        /// <summary>
        /// Records the id the world assigned. A pinned id is never replaced: it is kept apart in
        /// _entityId, so an entity that leaves and registers again is still known to be numbered
        /// automatically.
        /// </summary>
        internal void SetEntityIdInternal(int entityId)
        {
            _assignedEntityId = entityId;
        }

        /// <summary>
        /// Writes this entity's state into a snapshot slot. The pose written is the corrected one --
        /// see <see cref="CorrectedPosition"/> -- which is the Rigidbody's own whenever nothing is
        /// being eased in, and always on the authority.
        /// </summary>
        public virtual void SaveState(PhysicsSnapshot snapshot, int index)
        {
            if (snapshot == null)
                return;

            snapshot.EntityIds[index] = EntityId;
            snapshot.Positions[index] = CorrectedPosition;
            snapshot.Rotations[index] = CorrectedRotation;
            snapshot.Velocities[index] = Velocity;
            snapshot.AngularVelocities[index] = AngularVelocity;
            snapshot.InputLagFrames[index] = 0;
        }

        /// <summary>
        /// Writes a snapshot slot straight onto this entity. A teleport: anything being eased in is
        /// dropped along with the state it was correcting.
        /// </summary>
        public virtual void RestoreState(PhysicsSnapshot snapshot, int index)
        {
            if (snapshot == null)
                return;

            Position = snapshot.Positions[index];
            Rotation = snapshot.Rotations[index];
            Velocity = snapshot.Velocities[index];
            AngularVelocity = snapshot.AngularVelocities[index];
        }

        /// <summary>
        /// Corrects this entity toward a state it should have right now. The arguments must describe
        /// the present: a state as the authority captured it is the network delay old, and correcting
        /// toward that drags a moving entity backwards. <see cref="RollbackManager"/> works out the
        /// present-time correction from a received state and calls <see cref="ApplyCorrectionDelta"/>.
        /// </summary>
        public virtual void ApplyCorrection(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            Vector3 positionDelta = position - CorrectedPosition;
            Quaternion rotationDelta = rotation * Quaternion.Inverse(CorrectedRotation);

            ApplyCorrectionDelta(positionDelta, rotationDelta, velocity - Velocity, angularVelocity - AngularVelocity);
        }

        /// <summary>
        /// Applies a measured prediction error to this entity as it stands now. Velocities change at
        /// once. A pose error up to <see cref="CorrectionSnapDistance"/> is eased in over the following
        /// physics steps; a larger one is worth the visible jump.
        /// </summary>
        /// <param name="positionDelta">Authoritative position minus predicted position.</param>
        /// <param name="rotationDelta">World-space rotation taking the predicted rotation to the authoritative one.</param>
        /// <param name="velocityDelta">Authoritative velocity minus predicted velocity.</param>
        /// <param name="angularVelocityDelta">Authoritative angular velocity minus predicted angular velocity.</param>
        public virtual void ApplyCorrectionDelta(Vector3 positionDelta, Quaternion rotationDelta, Vector3 velocityDelta, Vector3 angularVelocityDelta)
        {
            // Velocities are not eased: a body left on a velocity already known to be wrong keeps
            // building position error for as long as the easing lasts.
            if (velocityDelta.sqrMagnitude > 0f)
                Velocity = Velocity + velocityDelta;

            if (angularVelocityDelta.sqrMagnitude > 0f)
                AngularVelocity = AngularVelocity + angularVelocityDelta;

            // Judged on everything owed, not on this correction alone, so a run of small corrections
            // cannot quietly add up to a jump that should have been taken at once.
            float owed = (PendingCorrectionPosition + positionDelta).magnitude;

            if (_rb == null || CorrectionBlendRate >= 1f || CorrectionSnapDistance <= 0f || owed > CorrectionSnapDistance)
            {
                // Read both before writing either: the setters drop what is pending.
                Vector3 position = CorrectedPosition + positionDelta;
                Quaternion rotation = rotationDelta * CorrectedRotation;

                Position = position;
                Rotation = rotation;
                return;
            }

            if (_correctionBlender == null)
                _correctionBlender = new CorrectionBlender();

            _correctionBlender.PositionBlendRate = CorrectionBlendRate;
            _correctionBlender.RotationBlendRate = CorrectionBlendRate;
            _correctionBlender.AddOffset(positionDelta, rotationDelta);
        }

        // Collision hooks for derived entities. Nothing in this library overrides them and nothing
        // tracks collisions centrally; NetBall raises them from its own collision handling.
        public virtual void OnPredictedCollision(NetPhysicsEntity other, Vector3 point) { }
        public virtual void OnConfirmedCollision(NetPhysicsEntity other, Vector3 point) { }
    }
}
