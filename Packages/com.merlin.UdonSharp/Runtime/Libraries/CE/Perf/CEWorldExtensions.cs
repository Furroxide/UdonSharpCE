using System;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Extension methods and typed helpers for CEWorld.
    ///
    /// Provides convenient typed access to component arrays and common operations.
    /// These methods wrap the generic object-based CEWorld API with type-safe versions.
    /// </summary>
    [PublicAPI]
    public static class CEWorldExtensions
    {
        #region Typed Component Registration

        /// <summary>
        /// Registers an int component array.
        /// </summary>
        public static int RegisterIntComponent(this CEWorld world, int typeId, int[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a float component array.
        /// </summary>
        public static int RegisterFloatComponent(this CEWorld world, int typeId, float[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a bool component array.
        /// </summary>
        public static int RegisterBoolComponent(this CEWorld world, int typeId, bool[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a Vector3 component array.
        /// </summary>
        public static int RegisterVector3Component(this CEWorld world, int typeId, Vector3[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a Vector2 component array.
        /// </summary>
        public static int RegisterVector2Component(this CEWorld world, int typeId, Vector2[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a Quaternion component array.
        /// </summary>
        public static int RegisterQuaternionComponent(this CEWorld world, int typeId, Quaternion[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a Color component array.
        /// </summary>
        public static int RegisterColorComponent(this CEWorld world, int typeId, Color[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a GameObject component array.
        /// </summary>
        public static int RegisterGameObjectComponent(this CEWorld world, int typeId, GameObject[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        /// <summary>
        /// Registers a Transform component array.
        /// </summary>
        public static int RegisterTransformComponent(this CEWorld world, int typeId, Transform[] array)
        {
            return world.RegisterComponent(typeId, array);
        }

        #endregion

        #region Typed Component Access

        /// <summary>
        /// Gets an int component array by slot.
        /// </summary>
        public static int[] GetIntArray(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as int[];
        }

        /// <summary>
        /// Gets a float component array by slot.
        /// </summary>
        public static float[] GetFloatArray(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as float[];
        }

        /// <summary>
        /// Gets a bool component array by slot.
        /// </summary>
        public static bool[] GetBoolArray(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as bool[];
        }

        /// <summary>
        /// Gets a Vector3 component array by slot.
        /// </summary>
        public static Vector3[] GetVector3Array(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as Vector3[];
        }

        /// <summary>
        /// Gets a Vector2 component array by slot.
        /// </summary>
        public static Vector2[] GetVector2Array(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as Vector2[];
        }

        /// <summary>
        /// Gets a Quaternion component array by slot.
        /// </summary>
        public static Quaternion[] GetQuaternionArray(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as Quaternion[];
        }

        /// <summary>
        /// Gets a Color component array by slot.
        /// </summary>
        public static Color[] GetColorArray(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as Color[];
        }

        /// <summary>
        /// Gets a GameObject component array by slot.
        /// </summary>
        public static GameObject[] GetGameObjectArray(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as GameObject[];
        }

        /// <summary>
        /// Gets a Transform component array by slot.
        /// </summary>
        public static Transform[] GetTransformArray(this CEWorld world, int slot)
        {
            return world.GetComponentArray(slot) as Transform[];
        }

        #endregion

        #region Entity Creation Helpers

        /// <summary>
        /// Creates an entity with initial components.
        /// </summary>
        /// <param name="world">The world.</param>
        /// <param name="componentSlots">Component slots to add.</param>
        /// <returns>The new entity ID.</returns>
        public static int CreateEntityWith(this CEWorld world, params int[] componentSlots)
        {
            int entity = world.CreateEntity();
            if (entity == CEWorld.InvalidEntity) return entity;

            for (int i = 0; i < componentSlots.Length; i++)
            {
                world.AddComponent(entity, componentSlots[i]);
            }

            return entity;
        }

        /// <summary>
        /// Creates multiple entities at once.
        /// </summary>
        /// <param name="world">The world.</param>
        /// <param name="count">Number of entities to create.</param>
        /// <param name="result">Array to fill with entity IDs.</param>
        /// <returns>Number of entities created.</returns>
        public static int CreateEntities(this CEWorld world, int count, int[] result)
        {
            if (result == null) return 0;

            int created = 0;
            int maxCount = Mathf.Min(count, result.Length);

            for (int i = 0; i < maxCount; i++)
            {
                int entity = world.CreateEntity();
                if (entity == CEWorld.InvalidEntity) break;
                result[i] = entity;
                created++;
            }

            return created;
        }

        #endregion

        #region Query Helpers

        /// <summary>
        /// Creates a new query for this world.
        /// </summary>
        public static CEQuery Query(this CEWorld world)
        {
            return new CEQuery(world);
        }

        /// <summary>
        /// Creates a query requiring specific components.
        /// </summary>
        public static CEQuery QueryWith(this CEWorld world, params int[] componentSlots)
        {
            var query = new CEQuery(world);
            for (int i = 0; i < componentSlots.Length; i++)
            {
                query.With(componentSlots[i]);
            }
            return query;
        }

        #endregion

        #region Batch Operations

        /// <summary>
        /// Destroys all entities matching a component mask.
        /// </summary>
        /// <param name="world">The world.</param>
        /// <param name="requiredMask">Component mask to match.</param>
        /// <returns>Number of entities destroyed.</returns>
        public static int DestroyEntitiesWithMask(this CEWorld world, int requiredMask)
        {
            int destroyed = 0;
            int maxEntities = world.MaxEntities;

            for (int i = 0; i < maxEntities; i++)
            {
                if (world.IsValidEntity(i) &&
                    (world.GetComponentMask(i) & requiredMask) == requiredMask)
                {
                    world.DestroyEntity(i);
                    destroyed++;
                }
            }

            return destroyed;
        }

        /// <summary>
        /// Adds a component to all entities matching a mask.
        /// </summary>
        public static int AddComponentToEntities(this CEWorld world, int requiredMask, int componentSlot)
        {
            int count = 0;
            int maxEntities = world.MaxEntities;

            for (int i = 0; i < maxEntities; i++)
            {
                if (world.IsValidEntity(i) &&
                    (world.GetComponentMask(i) & requiredMask) == requiredMask)
                {
                    world.AddComponent(i, componentSlot);
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Removes a component from all entities.
        /// </summary>
        public static int RemoveComponentFromAll(this CEWorld world, int componentSlot)
        {
            int count = 0;
            int maxEntities = world.MaxEntities;

            for (int i = 0; i < maxEntities; i++)
            {
                if (world.HasComponent(i, componentSlot))
                {
                    world.RemoveComponent(i, componentSlot);
                    count++;
                }
            }

            return count;
        }

        #endregion
    }
}
