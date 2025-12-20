using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Base class for any network-simulated physics object.
    /// </summary>
    [PublicAPI]
    public abstract class NetPhysicsEntity : UdonSharpBehaviour
    {
        [Header("NetPhysics")]
        public NetPhysicsWorld World;
        [SerializeField] private int _entityId = -1;
        [SerializeField] private bool _alwaysRelevant;
        [SerializeField] private Rigidbody _rb;

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

        public virtual void ApplyCorrection(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
        }

        public virtual void OnPredictedCollision(NetPhysicsEntity other, Vector3 point) { }
        public virtual void OnConfirmedCollision(NetPhysicsEntity other, Vector3 point) { }
    }
}
