using JetBrains.Annotations;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Calculates sync priority for entities based on gameplay relevance.
    /// Used to decide which entities to include more frequently in state broadcasts.
    /// </summary>
    [PublicAPI]
    public class SyncPrioritizer
    {
        public float BallPriority = 1.0f;
        public float LocalVehiclePriority = 0.9f;
        public float OtherVehiclePriority = 0.6f;
        public float ProjectilePriority = 0.3f;

        public float DistanceMax = 100f;
        public float SpeedMax = 20f;

        public float CalculatePriority(NetPhysicsEntity entity, VRCPlayerApi localPlayer)
        {
            if (entity == null)
                return 0f;

            float priority;
            switch (entity.EntityType)
            {
                case EntityType.Ball:
                    priority = BallPriority;
                    break;
                case EntityType.LocalVehicle:
                    priority = LocalVehiclePriority;
                    break;
                case EntityType.OtherVehicle:
                    priority = OtherVehiclePriority;
                    break;
                case EntityType.Projectile:
                    priority = ProjectilePriority;
                    break;
                default:
                    priority = 0.1f;
                    break;
            }

            if (localPlayer != null && localPlayer.IsValid())
            {
                float distance = Vector3.Distance(entity.transform.position, localPlayer.GetPosition());
                float distanceFactor = Mathf.Clamp01(1f - (distance / Mathf.Max(0.01f, DistanceMax)));
                priority *= (0.5f + 0.5f * distanceFactor);
            }

            float speed = entity.Velocity.magnitude;
            float speedFactor = Mathf.Clamp01(speed / Mathf.Max(0.01f, SpeedMax));
            priority *= (0.7f + 0.3f * speedFactor);

            if (entity.LastInteractionTime > Time.time - 1f)
                priority = Mathf.Min(1f, priority * 1.5f);

            return Mathf.Clamp01(priority);
        }
    }
}

