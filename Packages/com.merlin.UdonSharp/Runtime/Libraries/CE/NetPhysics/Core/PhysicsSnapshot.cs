using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Captures and restores entity physics state for prediction and rollback.
    /// Uses parallel arrays for fast iteration and compact serialization.
    /// </summary>
    [PublicAPI]
    public class PhysicsSnapshot
    {
        // Metadata
        public int Frame;
        public float Timestamp;
        public int EntityCount;

        // Entity state (parallel arrays)
        public int[] EntityIds;
        public Vector3[] Positions;
        public Quaternion[] Rotations;
        public Vector3[] Velocities;
        public Vector3[] AngularVelocities;

        public PhysicsSnapshot(int maxEntities = 0)
        {
            if (maxEntities > 0)
                Initialize(maxEntities);
        }

        public void Initialize(int maxEntities)
        {
            if (maxEntities <= 0)
                maxEntities = 1;

            if (EntityIds != null && EntityIds.Length >= maxEntities)
                return;

            EntityIds = new int[maxEntities];
            Positions = new Vector3[maxEntities];
            Rotations = new Quaternion[maxEntities];
            Velocities = new Vector3[maxEntities];
            AngularVelocities = new Vector3[maxEntities];
        }

        public void Clear()
        {
            Frame = 0;
            Timestamp = 0f;
            EntityCount = 0;
        }

        public void Capture(NetPhysicsEntity[] entities, int count)
        {
            if (entities == null)
            {
                EntityCount = 0;
                return;
            }

            EnsureCapacity(count);
            EntityCount = count;

            for (int i = 0; i < count; i++)
            {
                var entity = entities[i];
                if (entity == null)
                    continue;

                EntityIds[i] = entity.EntityId;
                Positions[i] = entity.Position;
                Rotations[i] = entity.Rotation;
                Velocities[i] = entity.Velocity;
                AngularVelocities[i] = entity.AngularVelocity;
            }
        }

        public void Restore(NetPhysicsEntity[] entities, int count)
        {
            if (entities == null)
                return;

            int restoreCount = Mathf.Min(count, EntityCount);

            // Fast path: assume entity ordering matches.
            bool orderingMatches = true;
            for (int i = 0; i < restoreCount; i++)
            {
                var entity = entities[i];
                if (entity == null)
                    continue;

                if (entity.EntityId != EntityIds[i])
                {
                    orderingMatches = false;
                    break;
                }
            }

            if (orderingMatches)
            {
                for (int i = 0; i < restoreCount; i++)
                {
                    var entity = entities[i];
                    if (entity == null)
                        continue;

                    entity.Position = Positions[i];
                    entity.Rotation = Rotations[i];
                    entity.Velocity = Velocities[i];
                    entity.AngularVelocity = AngularVelocities[i];
                }

                return;
            }

            // Fallback: map by EntityId (small counts, acceptable O(n^2)).
            for (int i = 0; i < EntityCount; i++)
            {
                int id = EntityIds[i];
                int entityIndex = FindEntityIndex(entities, count, id);
                if (entityIndex < 0)
                    continue;

                var entity = entities[entityIndex];
                if (entity == null)
                    continue;

                entity.Position = Positions[i];
                entity.Rotation = Rotations[i];
                entity.Velocity = Velocities[i];
                entity.AngularVelocity = AngularVelocities[i];
            }
        }

        public void CopyFrom(PhysicsSnapshot other)
        {
            if (other == null)
                return;

            EnsureCapacity(other.EntityCount);

            Frame = other.Frame;
            Timestamp = other.Timestamp;
            EntityCount = other.EntityCount;

            for (int i = 0; i < EntityCount; i++)
            {
                EntityIds[i] = other.EntityIds[i];
                Positions[i] = other.Positions[i];
                Rotations[i] = other.Rotations[i];
                Velocities[i] = other.Velocities[i];
                AngularVelocities[i] = other.AngularVelocities[i];
            }
        }

        public float CalculateDivergence(PhysicsSnapshot other)
        {
            if (other == null)
                return float.PositiveInfinity;

            int countA = EntityCount;
            int countB = other.EntityCount;
            int count = Mathf.Min(countA, countB);

            // Fast path: same ordering.
            bool orderingMatches = countA == countB;
            if (orderingMatches)
            {
                for (int i = 0; i < count; i++)
                {
                    if (EntityIds[i] != other.EntityIds[i])
                    {
                        orderingMatches = false;
                        break;
                    }
                }
            }

            float maxDist = 0f;

            if (orderingMatches)
            {
                for (int i = 0; i < count; i++)
                {
                    float dist = Vector3.Distance(Positions[i], other.Positions[i]);
                    if (dist > maxDist)
                        maxDist = dist;
                }

                return maxDist;
            }

            // Fallback: compare by EntityId (supports partial snapshots).
            for (int i = 0; i < countA; i++)
            {
                int id = EntityIds[i];
                int j = FindIndexById(other, id);
                if (j < 0)
                    continue;

                float dist = Vector3.Distance(Positions[i], other.Positions[j]);
                if (dist > maxDist)
                    maxDist = dist;
            }

            return maxDist;
        }

        public byte[] Serialize(StateCompressor compressor)
        {
            if (compressor == null)
                return null;

            return compressor.Compress(this);
        }

        public void Deserialize(byte[] data, StateCompressor compressor)
        {
            if (compressor == null || data == null)
                return;

            compressor.Decompress(data, this);
        }

        private void EnsureCapacity(int count)
        {
            if (EntityIds == null || EntityIds.Length < count)
                Initialize(Mathf.Max(1, count));
        }

        private static int FindEntityIndex(NetPhysicsEntity[] entities, int count, int entityId)
        {
            for (int i = 0; i < count; i++)
            {
                var e = entities[i];
                if (e != null && e.EntityId == entityId)
                    return i;
            }

            return -1;
        }

        private static int FindIndexById(PhysicsSnapshot snapshot, int entityId)
        {
            int count = snapshot.EntityCount;
            for (int i = 0; i < count; i++)
            {
                if (snapshot.EntityIds[i] == entityId)
                    return i;
            }
            return -1;
        }
    }
}
