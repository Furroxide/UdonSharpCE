using JetBrains.Annotations;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Calculates sync priority for entities based on gameplay relevance, and schedules which of them
    /// go into each state broadcast when a packet cannot carry them all.
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

        /// <summary>
        /// Least an entity's send debt grows per broadcast, whatever its priority works out to. It is
        /// what bounds how long anything can wait: a priority of zero would otherwise never be sent.
        /// </summary>
        public float MinimumSendPriority = 0.02f;

        /// <summary>
        /// Chooses which entities go into one packet. Returns how many indices were written to
        /// <paramref name="selected"/>, highest debt first.
        /// </summary>
        /// <remarks>
        /// Filling each packet with whatever ranked highest right now starved the rest for good: with
        /// one more entity than a packet holds, the slowest car furthest from the authority was never
        /// sent at all and so never corrected on any client. Priority is therefore a rate, not a rank.
        /// Every broadcast adds each entity's priority to a running debt, the packet takes the largest
        /// debts, and being sent clears the debt -- so a low priority means being sent less often
        /// instead of never, and how often follows the priorities.
        /// </remarks>
        /// <param name="priorities">This broadcast's priority per entity; negative marks an entry that cannot be sent.</param>
        /// <param name="debt">Running debt per entity, carried between broadcasts by the caller.</param>
        /// <param name="count">Entities described by the two arrays.</param>
        /// <param name="maxCount">Entities one packet can carry.</param>
        /// <param name="selected">Receives the chosen indices.</param>
        public int SelectForPacket(float[] priorities, float[] debt, int count, int maxCount, int[] selected)
        {
            if (priorities == null || debt == null || selected == null)
                return 0;

            if (count > priorities.Length)
                count = priorities.Length;
            if (count > debt.Length)
                count = debt.Length;
            if (maxCount > selected.Length)
                maxCount = selected.Length;

            for (int i = 0; i < count; i++)
            {
                // A negative debt is this method's own marker from an earlier call, never a balance.
                if (debt[i] < 0f)
                    debt[i] = 0f;

                float priority = priorities[i];
                if (priority < 0f)
                {
                    debt[i] = -1f;
                    continue;
                }

                debt[i] += priority > MinimumSendPriority ? priority : MinimumSendPriority;
            }

            int written = 0;
            while (written < maxCount)
            {
                int best = -1;
                float bestDebt = 0f;

                for (int i = 0; i < count; i++)
                {
                    float owed = debt[i];
                    if (owed < 0f)
                        continue;
                    if (best >= 0 && owed <= bestDebt)
                        continue;

                    bestDebt = owed;
                    best = i;
                }

                if (best < 0)
                    break;

                selected[written] = best;
                written++;

                // Marked rather than cleared so the scan above cannot pick it twice.
                debt[best] = -1f;
            }

            for (int i = 0; i < written; i++)
                debt[selected[i]] = 0f;

            return written;
        }

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

