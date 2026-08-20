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
        [SerializeField] private int _entityId = -1;
        [SerializeField] private bool _alwaysRelevant;
        [SerializeField] private Rigidbody _rb;

        [Header("Correction")]
        [Tooltip("Divergence in metres above which a correction is snapped instead of eased in.")]
        public float CorrectionSnapDistance = 2f;
        [Tooltip("Fraction of the remaining error removed per physics step while easing a correction in. 1 snaps.")]
        [Range(0.01f, 1f)] public float CorrectionBlendRate = 0.2f;

        private CorrectionBlender _correctionBlender;

        protected Rigidbody RigidbodyComponent => _rb;

        public int EntityId => _entityId;
        public virtual EntityType EntityType => EntityType.Unknown;
        public bool AlwaysRelevant => _alwaysRelevant;

        public float LastInteractionTime { get; protected set; }

        public virtual Vector3 Position
        {
            get => _rb != null ? _rb.position : transform.position;
            set
            {
                if (_rb != null)
                    _rb.position = value;
                else
                    transform.position = value;
            }
        }

        public virtual Quaternion Rotation
        {
            get => _rb != null ? _rb.rotation : transform.rotation;
            set
            {
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
            if (_correctionBlender != null && _rb != null)
                _correctionBlender.ApplyBlend(_rb);
        }

        internal void SetEntityIdInternal(int entityId)
        {
            _entityId = entityId;
        }

        public virtual void SaveState(PhysicsSnapshot snapshot, int index)
        {
            if (snapshot == null)
                return;

            snapshot.EntityIds[index] = EntityId;
            snapshot.Positions[index] = Position;
            snapshot.Rotations[index] = Rotation;
            snapshot.Velocities[index] = Velocity;
            snapshot.AngularVelocities[index] = AngularVelocity;
        }

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
        /// Applies an authoritative state received from the network. Small divergences are eased in
        /// over the following physics steps; only a divergence past <see cref="CorrectionSnapDistance"/>
        /// is worth the visible jump. Writing the transform straight from the packet, which is what
        /// this used to do, made every remote object teleport several times a second.
        /// </summary>
        public virtual void ApplyCorrection(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            // Angular velocity is not blended: a rigidbody spinning at the wrong rate reads as wrong
            // far faster than one sitting a few centimetres off, and it carries no visible pop.
            AngularVelocity = angularVelocity;

            float error = Vector3.Distance(Position, position);

            if (_rb == null || CorrectionBlendRate >= 1f || CorrectionSnapDistance <= 0f || error > CorrectionSnapDistance)
            {
                Position = position;
                Rotation = rotation;
                Velocity = velocity;

                if (_correctionBlender != null)
                    _correctionBlender.ClearTarget();

                return;
            }

            if (_correctionBlender == null)
                _correctionBlender = new CorrectionBlender();

            _correctionBlender.PositionBlendRate = CorrectionBlendRate;
            _correctionBlender.RotationBlendRate = CorrectionBlendRate;
            _correctionBlender.VelocityBlendRate = Mathf.Min(1f, CorrectionBlendRate * 1.5f);
            _correctionBlender.SetTarget(position, rotation, velocity);
        }

        public virtual void OnPredictedCollision(NetPhysicsEntity other, Vector3 point) { }
        public virtual void OnConfirmedCollision(NetPhysicsEntity other, Vector3 point) { }
    }
}
