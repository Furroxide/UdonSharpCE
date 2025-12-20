using JetBrains.Annotations;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Shared high-priority physics object (e.g., a ball).
    /// Typically master-authoritative, but predicted on clients for responsive hits.
    /// </summary>
    [PublicAPI]
    public class NetBall : NetPhysicsEntity
    {
        [Header("Ball")]
        [SerializeField] private float _radius = 0.5f;
        [SerializeField] private float _bounciness = 0.6f;

        public override EntityType EntityType => EntityType.Ball;

        public float Radius
        {
            get => _radius;
            set => _radius = value;
        }

        public float Bounciness
        {
            get => _bounciness;
            set => _bounciness = value;
        }

        public VRCPlayerApi LastTouchedBy { get; private set; }
        public float LastTouchTime { get; private set; }

        public void OnHit(NetVehicle vehicle, Vector3 hitPoint, Vector3 hitNormal, float force)
        {
            if (vehicle != null)
                LastTouchedBy = Networking.GetOwner(vehicle.gameObject);

            LastTouchTime = Time.time;
            LastInteractionTime = LastTouchTime;
        }

        public void OnGoalScored(int team) { }
    }
}

