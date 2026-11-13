using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Entity physics state at one simulation frame, for prediction history and for the wire.
    /// Uses parallel arrays for fast iteration and compact serialization.
    /// </summary>
    [PublicAPI]
    public class PhysicsSnapshot
    {
        /// <summary>
        /// <see cref="InputLagFrames"/> value for "the authority could not say".
        /// </summary>
        public const int UnknownInputLag = -1;

        /// <summary>
        /// <see cref="EntityIds"/> value for a slot that describes no entity.
        /// </summary>
        public const int InvalidEntityId = -1;

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

        /// <summary>
        /// For a state received from the authority: how many frames behind <see cref="Frame"/> the
        /// newest driver input it had applied to this entity was stamped. Zero for anything that is not
        /// steered through the authority's input buffer, <see cref="UnknownInputLag"/> when the
        /// authority has no usable figure. Only the client driving that entity reads it.
        /// </summary>
        public int[] InputLagFrames;

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
            InputLagFrames = new int[maxEntities];
        }

        public void Clear()
        {
            Frame = 0;
            Timestamp = 0f;
            EntityCount = 0;
        }

        /// <summary>
        /// Records every entity through <see cref="NetPhysicsEntity.SaveState"/>, so what is stored is
        /// the pose each entity is being corrected toward rather than wherever its Rigidbody happens to
        /// be partway through easing a correction in.
        /// </summary>
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
                {
                    // Leaving the slot as it was let a destroyed entity's last id -- or the zero a fresh
                    // array starts with -- stand in for a live entity that happened to share it.
                    EntityIds[i] = InvalidEntityId;
                    InputLagFrames[i] = 0;
                    continue;
                }

                entity.SaveState(this, i);
            }
        }

        /// <summary>
        /// Writes this snapshot straight onto the entities, dropping any correction they were still
        /// easing in. This is a teleport; use it for resets, not for state received from the network.
        /// </summary>
        public void Restore(NetPhysicsEntity[] entities, int count)
        {
            Apply(entities, count, false);
        }

        /// <summary>
        /// Hands this snapshot to the entities as the pose they should have right now, so an entity
        /// can ease a small jump in instead of teleporting.
        /// </summary>
        /// <remarks>
        /// The snapshot has to describe the present. A state received from the authority describes the
        /// frame it was captured at, which is the network delay in the past: handing that over here
        /// drags every moving entity back to where it used to be. <see cref="RollbackManager"/> is what
        /// turns a received state into a present-time correction.
        /// </remarks>
        public void ApplyAsCorrection(NetPhysicsEntity[] entities, int count)
        {
            Apply(entities, count, true);
        }

        private void Apply(NetPhysicsEntity[] entities, int count, bool asCorrection)
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
                    ApplyToEntity(entities[i], i, asCorrection);
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

                ApplyToEntity(entities[entityIndex], i, asCorrection);
            }
        }

        private void ApplyToEntity(NetPhysicsEntity entity, int index, bool asCorrection)
        {
            if (entity == null)
                return;

            if (asCorrection)
            {
                entity.ApplyCorrection(Positions[index], Rotations[index], Velocities[index], AngularVelocities[index]);
                return;
            }

            entity.RestoreState(this, index);
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
                InputLagFrames[i] = other.InputLagFrames[i];
            }
        }

        /// <summary>
        /// Index of the entry describing <paramref name="entityId"/>, or -1.
        /// </summary>
        public int IndexOfEntity(int entityId)
        {
            int count = EntityCount;
            for (int i = 0; i < count; i++)
            {
                if (EntityIds[i] == entityId)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Moves one entry by a correction: positions and velocities add, the rotation is applied on top
        /// of the stored one in world space.
        /// </summary>
        public void OffsetEntity(int index, Vector3 positionDelta, Quaternion rotationDelta, Vector3 velocityDelta, Vector3 angularVelocityDelta)
        {
            if (index < 0 || index >= EntityCount)
                return;

            Positions[index] = Positions[index] + positionDelta;
            Rotations[index] = rotationDelta * Rotations[index];
            Velocities[index] = Velocities[index] + velocityDelta;
            AngularVelocities[index] = AngularVelocities[index] + angularVelocityDelta;
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
                int j = other.IndexOfEntity(id);
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

        /// <summary>
        /// Returns false when the packet was rejected, in which case this snapshot still holds
        /// whatever it held before.
        /// </summary>
        public bool Deserialize(byte[] data, StateCompressor compressor)
        {
            if (compressor == null || data == null)
                return false;

            return compressor.Decompress(data, this);
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
    }
}
