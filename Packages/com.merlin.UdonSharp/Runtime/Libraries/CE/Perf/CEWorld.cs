using JetBrains.Annotations;
using UdonSharp.CE.Core;
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
    ///     private Vector3[] positions;
    ///     private Vector3[] velocities;
    ///     private int positionSlot;
    ///     private int velocitySlot;
    ///
    ///     void Start()
    ///     {
    ///         world = new CEWorld(2000);
    ///         positions = new Vector3[world.MaxEntities];
    ///         velocities = new Vector3[world.MaxEntities];
    ///
    ///         // One call per array. The returned slot (0-31) is what AddComponent/HasComponent take.
    ///         positionSlot = world.RegisterComponent(ComponentTypeId.CustomBase + 0, positions);
    ///         velocitySlot = world.RegisterComponent(ComponentTypeId.CustomBase + 1, velocities);
    ///
    ///         // Systems are public, parameterless methods invoked by name.
    ///         world.RegisterSystem(this, nameof(MoveBullets));
    ///     }
    ///
    ///     void Update()
    ///     {
    ///         world.Tick();
    ///     }
    ///
    ///     public void MoveBullets() { /* iterate world.ActiveEntities */ }
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

        /// <summary>
        /// Flush queue for deferred destruction. Holds each queued id once; an entry whose entity is no
        /// longer PendingDestroy by the time the flush reaches it is skipped.
        /// </summary>
        private readonly int[] _pendingDestroyList;
        private int _pendingDestroyCount;

        /// <summary>
        /// Whether an id already has an entry in <see cref="_pendingDestroyList"/>.
        /// </summary>
        /// <remarks>
        /// The queue is only emptied by a flush, while an entity can be deferred, cancelled and deferred
        /// again -- or destroyed outright, recycled and deferred again -- any number of times in between.
        /// Appending on every deferral filled the queue with duplicates and stale ids until it was full,
        /// after which a deferral was dropped without being queued and that entity stayed PendingDestroy
        /// forever: never flushed, never returned to the free list. One entry per id cannot overflow.
        /// </remarks>
        private readonly bool[] _pendingDestroyQueued;

        /// <summary>
        /// Number of entities currently in the PendingDestroy state. Kept separately from the queue
        /// length, which also counts entries that were cancelled or destroyed immediately since.
        /// </summary>
        private int _pendingEntityCount;

        /// <summary>
        /// Dense list of active entity IDs for O(activeCount) iteration.
        /// </summary>
        private readonly int[] _activeList;
        
        /// <summary>
        /// Reverse mapping from entityId to index in _activeList (-1 if not active).
        /// </summary>
        private readonly int[] _entityToActiveIndex;

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
        private readonly CECallback[] _systems;

        /// <summary>
        /// Execution order for each system (lower runs first).
        /// </summary>
        private readonly int[] _systemOrders;

        /// <summary>
        /// System indices in the order Tick runs them.
        /// </summary>
        /// <remarks>
        /// Sorting permutes this array and nothing else. The system arrays stay in registration order
        /// because the index RegisterSystem hands back is how callers name a system afterwards: when the
        /// sort moved the systems themselves, that index pointed at a different system from the first
        /// Tick on, and DisableSystem switched off whichever one had been sorted into its place.
        /// </remarks>
        private readonly int[] _systemExecutionOrder;

        /// <summary>
        /// Whether each system is enabled.
        /// </summary>
        private readonly bool[] _systemEnabled;

        /// <summary>
        /// Number of registered systems.
        /// </summary>
        private int _systemCount;

        /// <summary>
        /// Whether systems need to be re-sorted.
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
        /// Gets the number of entities currently in the PendingDestroy state.
        /// Useful for debugging or deciding whether to call FlushPendingDestructions().
        /// </summary>
        /// <remarks>
        /// This is a live count: an entity stops being counted the moment it is destroyed immediately,
        /// disabled or has its destruction cancelled, not only when the next flush runs.
        /// </remarks>
        public int PendingDestroyCount => _pendingEntityCount;

        /// <summary>
        /// Gets the number of available entity slots (free list + unallocated).
        /// </summary>
        public int AvailableEntitySlots => _freeListCount + (_maxEntities - _nextEntitySlot);

        /// <summary>
        /// Gets the dense array of active entity IDs for O(activeCount) iteration.
        /// Use with <see cref="ActiveEntityCount"/> for bounds. Do not modify this array.
        /// </summary>
        /// <remarks>
        /// This enables efficient iteration patterns:
        /// <code>
        /// int[] active = world.ActiveEntities;
        /// int count = world.ActiveEntityCount;
        /// for (int i = 0; i &lt; count; i++)
        /// {
        ///     int entityId = active[i];
        ///     // Active as long as nothing in this loop destroys, defers, disables or creates entities
        /// }
        /// </code>
        ///
        /// The list is unordered and is edited in place. <see cref="DestroyEntity"/>,
        /// <see cref="DestroyEntityDeferred"/> and <see cref="DisableEntity"/> all take the entity out
        /// immediately by swap-remove: the last entry moves into the vacated index and the count drops by
        /// one. <see cref="CreateEntity"/>, <see cref="EnableEntity"/> and <see cref="CancelDestruction"/>
        /// append at the end. Entries at or beyond <see cref="ActiveEntityCount"/> are stale leftovers.
        ///
        /// So the forward loop above is only correct while the loop body leaves the entity set alone.
        /// Removing an entity the loop has not reached yet makes it visit the moved entry twice (once
        /// at its new index, once at its stale old one); removing the current one and then creating
        /// another makes it miss the moved entry. Re-reading ActiveEntityCount every pass does not
        /// help: removing the current entity then skips whichever one was moved into its place.
        ///
        /// What is safe when a loop has to remove entities:
        /// <list type="bullet">
        /// <item>Iterate backwards (<c>for (int i = count - 1; i &gt;= 0; i--)</c>) and remove only the
        /// entity being visited, or ones already visited. Every entry that moves comes from an index
        /// the loop has already passed.</item>
        /// <item>Copy the ids out first with <see cref="GetActiveEntities"/> or CEQuery.Execute, loop over
        /// the copy, and check <see cref="IsValidEntity"/> on each id before using it. This is the one to
        /// use when a system removes entities other than the current one. Remove them with
        /// <see cref="DestroyEntityDeferred"/> and leave the flush until the loop is done: the check only
        /// says whether an id is active, and an id destroyed outright can be handed to a new entity by a
        /// CreateEntity later in the same loop, which then passes the check in the old entity's place.
        /// A deferred id is not handed out again before the flush.</item>
        /// <item>Scan ids <c>0..MaxEntities</c> and test <see cref="GetEntityState"/>, which does not
        /// depend on this list at all.</item>
        /// </list>
        /// </remarks>
        public int[] ActiveEntities => _activeList;

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
            _pendingDestroyList = new int[maxEntities];
            _pendingDestroyQueued = new bool[maxEntities];
            _pendingDestroyCount = 0;
            _pendingEntityCount = 0;
            _activeEntityCount = 0;
            _nextEntitySlot = 0;
            
            // Dense active entity list for O(activeCount) iteration
            _activeList = new int[maxEntities];
            _entityToActiveIndex = new int[maxEntities];
            for (int i = 0; i < maxEntities; i++)
            {
                _entityToActiveIndex[i] = -1;
            }

            // Component storage
            _componentArrays = new object[MaxComponentTypes];
            _componentTypeIds = new int[MaxComponentTypes];
            _registeredComponentCount = 0;

            // System storage
            _systems = new CECallback[MaxSystems];
            _systemOrders = new int[MaxSystems];
            _systemExecutionOrder = new int[MaxSystems];
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
            
            // Add to dense active list
            _entityToActiveIndex[entityId] = _activeEntityCount;
            _activeList[_activeEntityCount] = entityId;
            _activeEntityCount++;

            return entityId;
        }

        /// <summary>
        /// Attempts to create a new entity without logging errors on failure.
        /// Useful for high-frequency spawning where capacity limits are expected.
        /// </summary>
        /// <param name="entityId">The created entity ID, or InvalidEntity if creation failed.</param>
        /// <returns>True if the entity was created successfully.</returns>
        public bool TryCreateEntity(out int entityId)
        {
            // Try to reuse from free list first
            if (_freeListCount > 0)
            {
                _freeListCount--;
                entityId = _freeList[_freeListCount];
            }
            else if (_nextEntitySlot < _maxEntities)
            {
                entityId = _nextEntitySlot;
                _nextEntitySlot++;
            }
            else
            {
                entityId = InvalidEntity;
                return false;
            }

            _entityStates[entityId] = EntityState.Active;
            _componentMasks[entityId] = 0;
            
            // Add to dense active list
            _entityToActiveIndex[entityId] = _activeEntityCount;
            _activeList[_activeEntityCount] = entityId;
            _activeEntityCount++;

            return true;
        }

        /// <summary>
        /// Destroys an entity immediately and clears its component mask.
        /// Works for Active, Disabled, and PendingDestroy entities.
        /// The entity ID is recycled and may be reused by future CreateEntity calls.
        /// </summary>
        /// <remarks>
        /// Only the mask is cleared. The values in the registered component arrays are left exactly as
        /// they were, so the next entity to be given this id finds the previous owner's data at its
        /// index. Whoever calls <see cref="AddComponent"/> on a new entity has to write that component's
        /// initial value; the world cannot do it, it does not know what a fresh value looks like.
        ///
        /// An Active entity is swap-removed from <see cref="ActiveEntities"/> here; see the remarks
        /// there before calling this from inside a loop over that list.
        /// </remarks>
        /// <param name="entityId">The entity to destroy.</param>
        /// <returns>True if the entity was destroyed, false if already free or invalid.</returns>
        public bool DestroyEntity(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            
            EntityState currentState = _entityStates[entityId];
            if (currentState == EntityState.Free) return false;

            // Remove from dense active list if was Active
            if (currentState == EntityState.Active)
            {
                RemoveFromActiveList(entityId);
            }
            else if (currentState == EntityState.PendingDestroy)
            {
                // Its queue entry stays behind and is skipped by the flush; only the live count moves.
                _pendingEntityCount--;
            }

            _entityStates[entityId] = EntityState.Free;
            _entityVersions[entityId]++;
            _componentMasks[entityId] = 0;

            // Add to free list for reuse
            if (_freeListCount < _freeList.Length)
            {
                _freeList[_freeListCount] = entityId;
                _freeListCount++;
            }
            
            return true;
        }

        /// <summary>
        /// Marks an entity for destruction at the end of the current tick.
        /// Idempotent: calling multiple times on the same entity has no additional effect.
        /// </summary>
        /// <remarks>
        /// What is deferred is the recycling, not the removal. The entity leaves
        /// <see cref="ActiveEntities"/> immediately, by the same swap-remove as
        /// <see cref="DestroyEntity"/>, and <see cref="ActiveEntityCount"/> drops at once. Until the
        /// flush at the end of <see cref="Tick"/> (or <see cref="FlushPendingDestructions"/>) its id is
        /// not handed out again and its component mask is kept, so code still holding the id this tick
        /// reads this entity's data rather than a newly created one's.
        ///
        /// It is therefore no safer than DestroyEntity for a loop over ActiveEntities itself: the list is
        /// reordered either way. Where it does matter is a loop over a copy of the ids, which relies on a
        /// removed id staying inactive for the rest of the loop. The remarks on
        /// <see cref="ActiveEntities"/> list the loops that hold up.
        /// </remarks>
        /// <param name="entityId">The entity to destroy.</param>
        /// <returns>True if the entity was marked for destruction, false if already pending or invalid.</returns>
        public bool DestroyEntityDeferred(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            
            EntityState currentState = _entityStates[entityId];
            
            // Already pending - idempotent, no duplicate entries
            if (currentState == EntityState.PendingDestroy) return false;
            
            // Only active entities can be deferred (disabled entities should be enabled first or destroyed immediately)
            if (currentState != EntityState.Active) return false;
            
            // Remove from dense active list
            RemoveFromActiveList(entityId);
            
            _entityStates[entityId] = EntityState.PendingDestroy;
            _pendingEntityCount++;
            
            // Queue for the flush, once per id. An id that was deferred earlier and then cancelled,
            // disabled or destroyed-and-recycled still has its entry, and that entry will do.
            if (!_pendingDestroyQueued[entityId])
            {
                _pendingDestroyQueued[entityId] = true;
                _pendingDestroyList[_pendingDestroyCount] = entityId;
                _pendingDestroyCount++;
            }
            
            return true;
        }

        /// <summary>
        /// Enables a previously disabled entity.
        /// </summary>
        /// <param name="entityId">The entity to enable.</param>
        /// <returns>True if the entity was enabled, false if not disabled or invalid.</returns>
        public bool EnableEntity(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            if (_entityStates[entityId] == EntityState.Disabled)
            {
                _entityStates[entityId] = EntityState.Active;
                
                // Add to dense active list
                _entityToActiveIndex[entityId] = _activeEntityCount;
                _activeList[_activeEntityCount] = entityId;
                _activeEntityCount++;
                
                return true;
            }
            return false;
        }

        /// <summary>
        /// Disables an entity. Disabled entities are skipped by systems but retain components.
        /// Can also be used to cancel a pending destruction (changes PendingDestroy to Disabled).
        /// </summary>
        /// <param name="entityId">The entity to disable.</param>
        /// <returns>True if the entity was disabled, false if invalid or already free.</returns>
        public bool DisableEntity(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            
            EntityState state = _entityStates[entityId];
            if (state == EntityState.Active)
            {
                // Remove from dense active list
                RemoveFromActiveList(entityId);
                _entityStates[entityId] = EntityState.Disabled;
                return true;
            }
            if (state == EntityState.PendingDestroy)
            {
                // PendingDestroy entities are already removed from dense list
                _entityStates[entityId] = EntityState.Disabled;
                _pendingEntityCount--;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Cancels a pending destruction, restoring the entity to Active state.
        /// </summary>
        /// <param name="entityId">The entity to restore.</param>
        /// <returns>True if the entity was restored, false if not pending destruction.</returns>
        public bool CancelDestruction(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            if (_entityStates[entityId] == EntityState.PendingDestroy)
            {
                _entityStates[entityId] = EntityState.Active;
                _pendingEntityCount--;
                
                // Add back to dense active list
                _entityToActiveIndex[entityId] = _activeEntityCount;
                _activeList[_activeEntityCount] = entityId;
                _activeEntityCount++;
                
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if an entity ID is valid and active.
        /// Use this for normal gameplay checks where you want to skip disabled/pending entities.
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
        /// Checks if an entity exists and is alive (Active or Disabled, but not Free or PendingDestroy).
        /// Use this when you need to access component data regardless of enabled state.
        /// </summary>
        /// <param name="entityId">The entity to check.</param>
        /// <returns>True if the entity is alive.</returns>
        public bool IsEntityAlive(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            EntityState state = _entityStates[entityId];
            return state == EntityState.Active || state == EntityState.Disabled;
        }

        /// <summary>
        /// Checks if an entity exists in any non-free state (Active, Disabled, or PendingDestroy).
        /// Use this for cleanup operations where you need to handle all existing entities.
        /// </summary>
        /// <param name="entityId">The entity to check.</param>
        /// <returns>True if the entity exists.</returns>
        public bool EntityExists(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            return _entityStates[entityId] != EntityState.Free;
        }

        /// <summary>
        /// Checks if an entity is pending destruction.
        /// </summary>
        /// <param name="entityId">The entity to check.</param>
        /// <returns>True if the entity is marked for deferred destruction.</returns>
        public bool IsPendingDestroy(int entityId)
        {
            if (entityId < 0 || entityId >= _maxEntities) return false;
            return _entityStates[entityId] == EntityState.PendingDestroy;
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
        /// Works for Active and Disabled entities.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="componentSlot">The component slot index.</param>
        /// <returns>True if the entity has the component.</returns>
        public bool HasComponent(int entityId, int componentSlot)
        {
            if (!IsEntityAlive(entityId)) return false;
            if (componentSlot < 0 || componentSlot >= MaxComponentTypes) return false;
            return (_componentMasks[entityId] & (1 << componentSlot)) != 0;
        }

        /// <summary>
        /// Adds a component to an entity (sets the component mask bit).
        /// Works for Active and Disabled entities.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="componentSlot">The component slot index.</param>
        public void AddComponent(int entityId, int componentSlot)
        {
            if (!IsEntityAlive(entityId)) return;
            if (componentSlot < 0 || componentSlot >= MaxComponentTypes) return;
            _componentMasks[entityId] |= (1 << componentSlot);
        }

        /// <summary>
        /// Removes a component from an entity (clears the component mask bit).
        /// Works for Active and Disabled entities.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="componentSlot">The component slot index.</param>
        public void RemoveComponent(int entityId, int componentSlot)
        {
            if (!IsEntityAlive(entityId)) return;
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
        /// <param name="target">The UdonSharpBehaviour that owns the system method.</param>
        /// <param name="methodName">The name of the method to call (use nameof()).</param>
        /// <param name="order">Execution order (lower runs first).</param>
        /// <returns>
        /// The system index, or -1 if registration failed. It identifies this system for
        /// EnableSystem/DisableSystem/IsSystemEnabled for the life of the world, whatever order the
        /// systems end up running in.
        /// </returns>
        /// <example>
        /// <code>
        /// world.RegisterSystem(this, nameof(UpdatePhysics), order: 10);
        /// world.RegisterSystem(this, nameof(UpdateRendering), order: 20);
        /// </code>
        /// </example>
        public int RegisterSystem(UdonSharpBehaviour target, string methodName, int order = 0)
        {
            if (target == null)
            {
                Debug.LogError("[CE.Perf] CEWorld: Cannot register system with null target");
                return -1;
            }

            if (string.IsNullOrEmpty(methodName))
            {
                Debug.LogError("[CE.Perf] CEWorld: Cannot register system with empty method name");
                return -1;
            }

            if (_systemCount >= MaxSystems)
            {
                Debug.LogError("[CE.Perf] CEWorld: Maximum systems exceeded");
                return -1;
            }

            int index = _systemCount;
            _systems[index] = new CECallback(target, methodName);
            _systemOrders[index] = order;
            _systemEnabled[index] = true;
            _systemExecutionOrder[index] = index;
            _systemCount++;
            _systemsDirty = true;

            return index;
        }

        /// <summary>
        /// Registers a system callback to be executed during Tick().
        /// </summary>
        /// <param name="callback">The callback to register.</param>
        /// <param name="order">Execution order (lower runs first).</param>
        /// <returns>The system index (stable, see the other overload), or -1 if registration failed.</returns>
        public int RegisterSystem(CECallback callback, int order = 0)
        {
            if (!callback.IsValid)
            {
                Debug.LogError("[CE.Perf] CEWorld: Cannot register invalid callback");
                return -1;
            }

            if (_systemCount >= MaxSystems)
            {
                Debug.LogError("[CE.Perf] CEWorld: Maximum systems exceeded");
                return -1;
            }

            int index = _systemCount;
            _systems[index] = callback;
            _systemOrders[index] = order;
            _systemEnabled[index] = true;
            _systemExecutionOrder[index] = index;
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

            // Execute systems in sorted order; the arrays themselves are indexed by registration
            for (int i = 0; i < _systemCount; i++)
            {
                int systemIndex = _systemExecutionOrder[i];
                if (_systemEnabled[systemIndex])
                {
                    _systems[systemIndex].Invoke();
                }
            }

            // Process deferred destructions
            ProcessPendingDestructionsInternal();
        }

        /// <summary>
        /// Processes all entities marked for deferred destruction immediately.
        /// Call this if you're not using Tick() but want to finalize pending destructions.
        /// Safe to call even when no entities are pending (O(1) early exit).
        /// </summary>
        /// <returns>The number of entities that were destroyed.</returns>
        public int FlushPendingDestructions()
        {
            return ProcessPendingDestructionsInternal();
        }

        /// <summary>
        /// Processes entities marked for deferred destruction.
        /// O(1) check when no entities pending, O(k) where k = pending count.
        /// </summary>
        /// <returns>The number of entities destroyed.</returns>
        private int ProcessPendingDestructionsInternal()
        {
            // Early exit - common case when no entities pending
            if (_pendingDestroyCount == 0) return 0;
            
            int pendingCount = _pendingDestroyCount;
            int[] pendingList = _pendingDestroyList;
            int destroyedCount = 0;
            
            for (int i = 0; i < pendingCount; i++)
            {
                int entityId = pendingList[i];
                _pendingDestroyQueued[entityId] = false;
                
                // Verify entity is still pending (could have been destroyed immediately via DestroyEntity
                // or cancelled via CancelDestruction/DisableEntity)
                if (_entityStates[entityId] == EntityState.PendingDestroy)
                {
                    _entityStates[entityId] = EntityState.Free;
                    _entityVersions[entityId]++;
                    _componentMasks[entityId] = 0;
                    _pendingEntityCount--;
                    // Note: _activeEntityCount was already decremented when entity was marked PendingDestroy
                    destroyedCount++;

                    if (_freeListCount < _freeList.Length)
                    {
                        _freeList[_freeListCount] = entityId;
                        _freeListCount++;
                    }
                }
            }
            
            _pendingDestroyCount = 0;
            return destroyedCount;
        }

        /// <summary>
        /// Sorts the execution order by system order value.
        /// Uses simple insertion sort (good for small arrays).
        /// </summary>
        /// <remarks>
        /// Only <see cref="_systemExecutionOrder"/> is rearranged. _systems, _systemOrders and
        /// _systemEnabled are never moved, so a system keeps the index it was registered under.
        /// </remarks>
        private void SortSystems()
        {
            // Insertion sort - stable and efficient for small arrays. Stable matters: systems sharing an
            // order value run in the order they were registered.
            for (int i = 1; i < _systemCount; i++)
            {
                int systemIndex = _systemExecutionOrder[i];
                int order = _systemOrders[systemIndex];

                int j = i - 1;
                while (j >= 0 && _systemOrders[_systemExecutionOrder[j]] > order)
                {
                    _systemExecutionOrder[j + 1] = _systemExecutionOrder[j];
                    j--;
                }

                _systemExecutionOrder[j + 1] = systemIndex;
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
                // Clearing destroys every live entity, so each one's version moves exactly as it would
                // for DestroyEntity. Ids are handed out again from 0 straight afterwards; without the
                // bump an (id, version) pair held from before the clear matched the unrelated entity
                // that was given the same id after it.
                if (_entityStates[i] != EntityState.Free)
                {
                    _entityVersions[i]++;
                }

                _entityStates[i] = EntityState.Free;
                _componentMasks[i] = 0;
                _entityToActiveIndex[i] = -1;
                _pendingDestroyQueued[i] = false;
            }

            _activeEntityCount = 0;
            _freeListCount = 0;
            _pendingDestroyCount = 0;
            _pendingEntityCount = 0;
            _nextEntitySlot = 0;
        }

        #region Internal Accessors for Hot Path Optimization
        
        /// <summary>
        /// Gets direct access to entity states array for hot path optimization.
        /// Only for use by CEQuery - do not modify.
        /// </summary>
        internal EntityState[] GetEntityStates() => _entityStates;
        
        /// <summary>
        /// Gets direct access to component masks array for hot path optimization.
        /// Only for use by CEQuery - do not modify.
        /// </summary>
        internal int[] GetComponentMasks() => _componentMasks;
        
        #endregion
        
        #region Private Helpers
        
        /// <summary>
        /// Removes an entity from the dense active list using swap-remove.
        /// O(1) operation. Also decrements _activeEntityCount.
        /// </summary>
        /// <param name="entityId">The entity to remove.</param>
        private void RemoveFromActiveList(int entityId)
        {
            int activeIdx = _entityToActiveIndex[entityId];
            if (activeIdx < 0) return; // Not in active list
            
            _activeEntityCount--;
            
            // If not the last element, swap with last
            if (activeIdx < _activeEntityCount)
            {
                int lastEntityId = _activeList[_activeEntityCount];
                _activeList[activeIdx] = lastEntityId;
                _entityToActiveIndex[lastEntityId] = activeIdx;
            }
            
            _entityToActiveIndex[entityId] = -1;
        }
        
        #endregion
        
        /// <summary>
        /// Gets all active entity IDs by copying from the dense active list.
        /// For zero-copy iteration, use <see cref="ActiveEntities"/> with <see cref="ActiveEntityCount"/> instead.
        /// </summary>
        /// <param name="result">Array to fill with entity IDs.</param>
        /// <returns>Number of entities written to result.</returns>
        public int GetActiveEntities(int[] result)
        {
            if (result == null) return 0;

            int copyCount = _activeEntityCount < result.Length ? _activeEntityCount : result.Length;
            for (int i = 0; i < copyCount; i++)
            {
                result[i] = _activeList[i];
            }

            return copyCount;
        }

        /// <summary>
        /// Counts entities matching a component mask.
        /// Uses dense active list for O(activeCount) iteration.
        /// </summary>
        /// <param name="requiredMask">Bitmask of required components.</param>
        /// <returns>Number of matching entities.</returns>
        public int CountEntitiesWithMask(int requiredMask)
        {
            int count = 0;
            int activeCount = _activeEntityCount;
            for (int i = 0; i < activeCount; i++)
            {
                int entityId = _activeList[i];
                if ((_componentMasks[entityId] & requiredMask) == requiredMask)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Gets entities matching a component mask.
        /// Uses dense active list for O(activeCount) iteration.
        /// </summary>
        /// <param name="requiredMask">Bitmask of required components.</param>
        /// <param name="result">Array to fill with matching entity IDs.</param>
        /// <returns>Number of entities written to result.</returns>
        public int GetEntitiesWithMask(int requiredMask, int[] result)
        {
            if (result == null) return 0;

            int count = 0;
            int maxCount = result.Length;
            int activeCount = _activeEntityCount;

            for (int i = 0; i < activeCount && count < maxCount; i++)
            {
                int entityId = _activeList[i];
                if ((_componentMasks[entityId] & requiredMask) == requiredMask)
                {
                    result[count] = entityId;
                    count++;
                }
            }

            return count;
        }

        #endregion
    }
}
