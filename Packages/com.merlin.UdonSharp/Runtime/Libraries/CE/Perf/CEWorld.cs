using System;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Entity-Component-System (ECS-Lite) world container.
    ///
    /// CEWorld manages entities and their component data using Structure-of-Arrays (SoA)
    /// storage for optimal performance in Udon's constrained environment.
    /// </summary>
    /// <remarks>
    /// Unlike traditional ECS implementations, CEWorld is designed specifically for Udon:
    /// - No heap allocations during gameplay (pre-allocated arrays)
    /// - No per-entity method call overhead (batched system execution)
    /// - No reflection or dynamic dispatch (compile-time component registration)
    ///
    /// The world stores components as parallel arrays. For example:
    /// - Entity 0 has Position at positions[0], Velocity at velocities[0]
    /// - Entity 1 has Position at positions[1], Velocity at velocities[1]
    ///
    /// This enables cache-friendly iteration and minimal overhead per entity.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class BulletHell : UdonSharpBehaviour
    /// {
    ///     private CEWorld world;
    ///
    ///     void Start()
    ///     {
    ///         world = new CEWorld(2000);
    ///         world.RegisterComponentArrays(
    ///             positions,      // Vector3[]
    ///             velocities,     // Vector3[]
    ///             lifetimes,      // float[]
    ///             active          // bool[]
    ///         );
    ///     }
    ///
    ///     void Update()
    ///     {
    ///         world.Tick();
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public class CEWorld
    {
        #region Constants

        /// <summary>
        /// Invalid entity ID, returned when entity creation fails.
        /// </summary>
        public const int InvalidEntity = -1;

        /// <summary>
        /// Maximum number of registered systems.
        /// </summary>
        private const int MaxSystems = 64;

        /// <summary>
        /// Maximum number of component types per world.
        /// </summary>
        private const int MaxComponentTypes = 32;

        #endregion

        #region Entity Storage

        /// <summary>
        /// Maximum entities this world can hold.
        /// </summary>
        private readonly int _maxEntities;

        /// <summary>
        /// State of each entity slot.
        /// </summary>
        private readonly EntityState[] _entityStates;

        /// <summary>
        /// Version numbers for entity validation (prevents use-after-destroy).
        /// </summary>
        private readonly int[] _entityVersions;

        /// <summary>
        /// Bitmask of active components per entity.
        /// </summary>
        private readonly int[] _componentMasks;

        /// <summary>
        /// Number of currently active entities.
        /// </summary>
        private int _activeEntityCount;

        /// <summary>
        /// Next entity slot to check for allocation.
        /// </summary>
        private int _nextEntitySlot;

        /// <summary>
        /// Free list for recycled entity slots.
        /// </summary>
        private readonly int[] _freeList;
        private int _freeListCount;

        #endregion

        #region Component Storage

        /// <summary>
        /// Component arrays indexed by type ID.
        /// Each entry is an array of the component type.
        /// </summary>
        private readonly object[] _componentArrays;

        /// <summary>
        /// Type IDs for each registered component slot.
        /// </summary>
        private readonly int[] _componentTypeIds;

        /// <summary>
        /// Number of registered component types.
        /// </summary>
        private int _registeredComponentCount;

        #endregion

        #region System Storage

        /// <summary>
        /// Registered system delegates.
        /// </summary>
        private readonly Action[] _systems;

        /// <summary>
        /// System execution order values.
        /// </summary>
        private readonly int[] _systemOrders;

        /// <summary>
        /// Whether each system is enabled.
        /// </summary>
        private readonly bool[] _systemEnabled;

        /// <summary>
        /// Number of registered systems.
        /// </summary>
        private int _systemCount;

        /// <summary>
        /// Whether systems need re-sorting after registration.
        /// </summary>
        private bool _systemsDirty;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the maximum entity capacity.
        /// </summary>
        public int MaxEntities => _maxEntities;

        /// <summary>
        /// Gets the current number of active entities.
        /// </summary>
        public int ActiveEntityCount => _activeEntityCount;

        /// <summary>
        /// Gets the number of registered component types.
        /// </summary>
        public int ComponentTypeCount => _registeredComponentCount;

        /// <summary>
        /// Gets the number of registered systems.
        /// </summary>
        public int SystemCount => _systemCount;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new ECS-Lite world with the specified capacity.
        /// </summary>
        /// <param name="maxEntities">Maximum number of entities. Default is 1000.</param>
        public CEWorld(int maxEntities = 1000)
        {
            if (maxEntities < 1) maxEntities = 1;
            if (maxEntities > 65535) maxEntities = 65535;

            _maxEntities = maxEntities;

            // Entity storage
            _entityStates = new EntityState[maxEntities];
            _entityVersions = new int[maxEntities];
            _componentMasks = new int[maxEntities];
            _freeList = new int[maxEntities];
            _freeListCount = 0;
            _activeEntityCount = 0;
            _nextEntitySlot = 0;

            // Component storage
            _componentArrays = new object[MaxComponentTypes];
            _componentTypeIds = new int[MaxComponentTypes];
            _registeredComponentCount = 0;

            // System storage
            _systems = new Action[MaxSystems];
            _systemOrders = new int[MaxSystems];
            _systemEnabled = new bool[MaxSystems];
            _systemCount = 0;
            _systemsDirty = false;
        }

        #endregion

        #region Entity Management

        /// <summary>
        /// Creates a new entity and returns its ID.
        /// </summary>
        /// <returns>Entity ID, or InvalidEntity if at capacity.</returns>
        public int CreateEntity()
        {
            int entityId;

            // Try to reuse from free list first
            if (_freeListCount > 0)
            {
                _freeListCount--;
                entityId = _freeList[_freeListCount];
            }
            else
            {
                // Find next free slot
                if (_nextEntitySlot >= _maxEntities)
                {
                    Debug.LogError("[CE.Perf] CEWorld: Entity capacity exceeded");
                    return InvalidEntity;
                }

                entityId = _nextEntitySlot;
                _nextEntitySlot++;
            }

            _entityStates[entityId] = EntityState.Active;
            _componentMasks[entityId] = 0;
            _activeEntityCount++;

            return entityId;
        }

        /// <summary>
        /// Destroys an entity and clears its component data.
        /// </summary>
        /// <param name="entityId">The entity to destroy.</param>
        public void DestroyEntity(int entityId)
        {
            if (!IsValidEntity(entityId)) return;

            _entityStates[entityId] = EntityState.Free;
            _entityVersions[entityId]++;
            _componentMasks[entityId] = 0;
            _activeEntityCount--;

            // Add to free list for reuse
            if (_freeListCount < _freeList.Length)
            {
                _freeList[_freeListCount] = entityId;
                _freeListCount++;
            }
        }

        /// <summary>
        /// Marks an entity for destruction at the end of the current tick.
        /// Safer than immediate destruction during system iteration.
        /// </summary>
        /// <param name="entityId">The entity to destroy.</param>
        public void DestroyEntityDeferred(int entityId)
        {
            if (!IsValidEntity(entityId)) return;
            _entityStates[entityId] = EntityState.PendingDestroy;
        }

        /// <summary>
        /// Enables a previously disabled entity.
        /// </summary>
        /// <param name="entityId">The entity to enable.</param>
        public void EnableEntity(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return;
            if (_entityStates[entityId] == EntityState.Disabled)
            {
                _entityStates[entityId] = EntityState.Active;
            }
        }

        /// <summary>
        /// Disables an entity. Disabled entities are skipped by systems but retain components.
        /// </summary>
        /// <param name="entityId">The entity to disable.</param>
        public void DisableEntity(int entityId)
        {
            if (!IsValidEntity(entityId)) return;
            _entityStates[entityId] = EntityState.Disabled;
        }

        /// <summary>
        /// Checks if an entity ID is valid and active.
        /// </summary>
        /// <param name="entityId">The entity to check.</param>
        /// <returns>True if the entity exists and is active.</returns>
        public bool IsValidEntity(int entityId)
        {
            return entityId >= 0 &&
                   entityId < _maxEntities &&
                   _entityStates[entityId] == EntityState.Active;
        }

        /// <summary>
        /// Gets the state of an entity.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <returns>The entity state.</returns>
        public EntityState GetEntityState(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities)
                return EntityState.Free;
            return _entityStates[entityId];
        }

        /// <summary>
        /// Gets the version number of an entity (incremented on each destroy).
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <returns>The entity version.</returns>
        public int GetEntityVersion(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities)
                return -1;
            return _entityVersions[entityId];
        }

        #endregion

        #region Component Management

        /// <summary>
        /// Registers a component array for a given type ID.
        /// </summary>
        /// <param name="typeId">The component type ID.</param>
        /// <param name="array">The pre-allocated array for this component type.</param>
        /// <returns>The component slot index, or -1 if registration failed.</returns>
        public int RegisterComponent(int typeId, object array)
        {
            if (_registeredComponentCount >= MaxComponentTypes)
            {
                Debug.LogError("[CE.Perf] CEWorld: Maximum component types exceeded");
                return -1;
            }

            int slot = _registeredComponentCount;
            _componentArrays[slot] = array;
            _componentTypeIds[slot] = typeId;
            _registeredComponentCount++;

            return slot;
        }

        /// <summary>
        /// Gets a component array by its slot index.
        /// </summary>
        /// <param name="slot">The component slot index.</param>
        /// <returns>The component array, or null if not found.</returns>
        public object GetComponentArray(int slot)
        {
            if (slot < 0 || slot >= _registeredComponentCount)
                return null;
            return _componentArrays[slot];
        }

        /// <summary>
        /// Gets the component slot index for a type ID.
        /// </summary>
        /// <param name="typeId">The component type ID.</param>
        /// <returns>The slot index, or -1 if not found.</returns>
        public int GetComponentSlot(int typeId)
        {
            for (int i = 0; i < _registeredComponentCount; i++)
            {
                if (_componentTypeIds[i] == typeId)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Checks if an entity has a specific component.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="componentSlot">The component slot index.</param>
        /// <returns>True if the entity has the component.</returns>
        public bool HasComponent(int entityId, int componentSlot)
        {
            if (!IsValidEntity(entityId)) return false;
            if (componentSlot < 0 || componentSlot >= MaxComponentTypes) return false;
            return (_componentMasks[entityId] & (1 << componentSlot)) != 0;
        }

        /// <summary>
        /// Adds a component to an entity (sets the component mask bit).
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="componentSlot">The component slot index.</param>
        public void AddComponent(int entityId, int componentSlot)
        {
            if (!IsValidEntity(entityId)) return;
            if (componentSlot < 0 || componentSlot >= MaxComponentTypes) return;
            _componentMasks[entityId] |= (1 << componentSlot);
        }

        /// <summary>
        /// Removes a component from an entity (clears the component mask bit).
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="componentSlot">The component slot index.</param>
        public void RemoveComponent(int entityId, int componentSlot)
        {
            if (!IsValidEntity(entityId)) return;
            if (componentSlot < 0 || componentSlot >= MaxComponentTypes) return;
            _componentMasks[entityId] &= ~(1 << componentSlot);
        }

        /// <summary>
        /// Gets the component mask for an entity.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <returns>The component bitmask.</returns>
        public int GetComponentMask(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities)
                return 0;
            return _componentMasks[entityId];
        }

        #endregion

        #region System Management

        /// <summary>
        /// Registers a system to be executed during Tick().
        /// </summary>
        /// <param name="system">The system action to register.</param>
        /// <param name="order">Execution order (lower runs first).</param>
        /// <returns>The system index, or -1 if registration failed.</returns>
        public int RegisterSystem(Action system, int order = 0)
        {
            if (system == null)
            {
                Debug.LogError("[CE.Perf] CEWorld: Cannot register null system");
                return -1;
            }

            if (_systemCount >= MaxSystems)
            {
                Debug.LogError("[CE.Perf] CEWorld: Maximum systems exceeded");
                return -1;
            }

            int index = _systemCount;
            _systems[index] = system;
            _systemOrders[index] = order;
            _systemEnabled[index] = true;
            _systemCount++;
            _systemsDirty = true;

            return index;
        }

        /// <summary>
        /// Enables a system by index.
        /// </summary>
        /// <param name="systemIndex">The system index.</param>
        public void EnableSystem(int systemIndex)
        {
            if (systemIndex >= 0 && systemIndex < _systemCount)
            {
                _systemEnabled[systemIndex] = true;
            }
        }

        /// <summary>
        /// Disables a system by index.
        /// </summary>
        /// <param name="systemIndex">The system index.</param>
        public void DisableSystem(int systemIndex)
        {
            if (systemIndex >= 0 && systemIndex < _systemCount)
            {
                _systemEnabled[systemIndex] = false;
            }
        }

        /// <summary>
        /// Checks if a system is enabled.
        /// </summary>
        /// <param name="systemIndex">The system index.</param>
        /// <returns>True if the system is enabled.</returns>
        public bool IsSystemEnabled(int systemIndex)
        {
            if (systemIndex < 0 || systemIndex >= _systemCount)
                return false;
            return _systemEnabled[systemIndex];
        }

        #endregion

        #region Tick / Update

        /// <summary>
        /// Executes all enabled systems in order.
        /// Call this once per frame from Update().
        /// </summary>
        public void Tick()
        {
            // Sort systems if needed
            if (_systemsDirty)
            {
                SortSystems();
                _systemsDirty = false;
            }

            // Execute systems
            for (int i = 0; i < _systemCount; i++)
            {
                if (_systemEnabled[i])
                {
                    _systems[i]();
                }
            }

            // Process deferred destructions
            ProcessPendingDestructions();
        }

        /// <summary>
        /// Processes entities marked for deferred destruction.
        /// </summary>
        private void ProcessPendingDestructions()
        {
            for (int i = 0; i < _maxEntities; i++)
            {
                if (_entityStates[i] == EntityState.PendingDestroy)
                {
                    _entityStates[i] = EntityState.Free;
                    _entityVersions[i]++;
                    _componentMasks[i] = 0;
                    _activeEntityCount--;

                    if (_freeListCount < _freeList.Length)
                    {
                        _freeList[_freeListCount] = i;
                        _freeListCount++;
                    }
                }
            }
        }

        /// <summary>
        /// Sorts systems by execution order.
        /// Uses simple insertion sort (good for small arrays).
        /// </summary>
        private void SortSystems()
        {
            // Insertion sort - stable and efficient for small arrays
            for (int i = 1; i < _systemCount; i++)
            {
                Action system = _systems[i];
                int order = _systemOrders[i];
                bool enabled = _systemEnabled[i];

                int j = i - 1;
                while (j >= 0 && _systemOrders[j] > order)
                {
                    _systems[j + 1] = _systems[j];
                    _systemOrders[j + 1] = _systemOrders[j];
                    _systemEnabled[j + 1] = _systemEnabled[j];
                    j--;
                }

                _systems[j + 1] = system;
                _systemOrders[j + 1] = order;
                _systemEnabled[j + 1] = enabled;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Clears all entities from the world.
        /// </summary>
        public void ClearEntities()
        {
            for (int i = 0; i < _maxEntities; i++)
            {
                _entityStates[i] = EntityState.Free;
                _componentMasks[i] = 0;
            }

            _activeEntityCount = 0;
            _freeListCount = 0;
            _nextEntitySlot = 0;
        }

        /// <summary>
        /// Gets all active entity IDs.
        /// </summary>
        /// <param name="result">Array to fill with entity IDs.</param>
        /// <returns>Number of entities written to result.</returns>
        public int GetActiveEntities(int[] result)
        {
            if (result == null) return 0;

            int count = 0;
            int maxCount = result.Length;

            for (int i = 0; i < _maxEntities && count < maxCount; i++)
            {
                if (_entityStates[i] == EntityState.Active)
                {
                    result[count] = i;
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Counts entities matching a component mask.
        /// </summary>
        /// <param name="requiredMask">Bitmask of required components.</param>
        /// <returns>Number of matching entities.</returns>
        public int CountEntitiesWithMask(int requiredMask)
        {
            int count = 0;
            for (int i = 0; i < _maxEntities; i++)
            {
                if (_entityStates[i] == EntityState.Active &&
                    (_componentMasks[i] & requiredMask) == requiredMask)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Gets entities matching a component mask.
        /// </summary>
        /// <param name="requiredMask">Bitmask of required components.</param>
        /// <param name="result">Array to fill with matching entity IDs.</param>
        /// <returns>Number of entities written to result.</returns>
        public int GetEntitiesWithMask(int requiredMask, int[] result)
        {
            if (result == null) return 0;

            int count = 0;
            int maxCount = result.Length;

            for (int i = 0; i < _maxEntities && count < maxCount; i++)
            {
                if (_entityStates[i] == EntityState.Active &&
                    (_componentMasks[i] & requiredMask) == requiredMask)
                {
                    result[count] = i;
                    count++;
                }
            }

            return count;
        }

        #endregion
    }
}
