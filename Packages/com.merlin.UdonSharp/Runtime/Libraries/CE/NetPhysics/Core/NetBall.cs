using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Shared high-priority physics object (e.g., a ball).
    /// Typically owner-authoritative, but predicted on clients for responsive hits.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class NetBall : NetPhysicsEntity
    {
        [Header("Ball")]
        [SerializeField] private float _radius = 0.5f;
        [SerializeField] private float _bounciness = 0.6f;

        // Who last touched the ball is a scoring fact, not a local observation: every client has to
        // agree on it. It used to be recorded only on whichever client happened to run OnHit, and
        // nothing ever called OnHit in the first place.
        [UdonSynced] private int _lastTouchedByPlayerId = -1;

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

        public int LastTouchedByPlayerId => _lastTouchedByPlayerId;

        public VRCPlayerApi LastTouchedBy =>
            _lastTouchedByPlayerId >= 0 ? VRCPlayerApi.GetPlayerById(_lastTouchedByPlayerId) : null;

        public float LastTouchTime { get; private set; }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision == null || collision.collider == null)
                return;

            NetVehicle vehicle = collision.collider.GetComponentInParent<NetVehicle>();
            if (vehicle == null)
                return;

            Vector3 ballPosition = transform.position;
            Vector3 vehiclePosition = vehicle.transform.position;
            Vector3 point = Vector3.Lerp(ballPosition, vehiclePosition, 0.5f);
            Vector3 normal = (ballPosition - vehiclePosition).normalized;

            OnHit(vehicle, point, normal, collision.relativeVelocity.magnitude);
        }

        public void OnHit(NetVehicle vehicle, Vector3 hitPoint, Vector3 hitNormal, float force)
        {
            LastTouchTime = Time.time;
            LastInteractionTime = LastTouchTime;

            if (vehicle == null)
                return;

            // Non-owners see the same contact, but only the owner's write survives, so they raise the
            // predicted-collision hook instead and wait for the owner's value to arrive.
            if (!Networking.IsOwner(gameObject))
            {
                OnPredictedCollision(vehicle, hitPoint);
                return;
            }

            int toucherId = vehicle.OwnerPlayerId;
            if (toucherId != _lastTouchedByPlayerId)
            {
                _lastTouchedByPlayerId = toucherId;
                RequestSerialization();
            }

            OnConfirmedCollision(vehicle, hitPoint);
        }

        public override void OnDeserialization()
        {
            // The owner only serializes the toucher when it changes, so arrival is itself the news.
            LastTouchTime = Time.time;
            LastInteractionTime = LastTouchTime;
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null || _lastTouchedByPlayerId < 0 || player.playerId != _lastTouchedByPlayerId)
                return;

            // Only the owner clears it, and it publishes the clear.
            //
            // OnPlayerLeft runs on every client, and _lastTouchedByPlayerId is [UdonSynced]. Letting every
            // client clear it locally looks free but is not: VRChat discards the write everywhere except
            // the owner, and the owner's next serialization then restores the departed id on all of them.
            // The value would flap between -1 and a stale id instead of settling.
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !Networking.IsOwner(localPlayer, gameObject))
                return;

            _lastTouchedByPlayerId = -1;
            RequestSerialization();
        }

        public void OnGoalScored(int team) { }
    }
}

