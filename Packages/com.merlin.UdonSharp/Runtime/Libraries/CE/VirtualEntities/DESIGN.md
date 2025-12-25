# CE.VirtualEntities Design Specification

## Overview

CE.VirtualEntities provides a Unity-native API for runtime-spawned networked entities in VRChat, working around VRC's limitation that objects instantiated at runtime cannot have network IDs assigned.

Instead of pooling pre-placed GameObjects, this system synchronizes **entity state** across the network, with each client maintaining local visual representations. This approach offers superior bandwidth efficiency, simpler ownership semantics, and a familiar Unity API.

---

## Design Goals

1. **Unity-Native API** - `Instantiate()`, `Destroy()`, `GetComponent<T>()` patterns identical to Unity
2. **Zero Boilerplate** - Works out of the box without complex setup
3. **Bandwidth Efficient** - Single sync point for all entities, quantized state compression
4. **Seamless Late-Join** - New players receive current world state automatically
5. **Scalable** - Support 100+ simultaneous entities within VRC network limits
6. **Extensible** - Custom components for game-specific behaviour
7. **Integrated** - Works with existing CE.NetPhysics, CE.Persistence, CE.Perf systems

---

## Architecture

### System Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        VirtualEntityWorld                           │
│  (Singleton UdonSharpBehaviour - Manual Sync Mode)                  │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │                    Networked State                           │   │
│  │  [UdonSynced] byte[] _entityState                           │   │
│  │  [UdonSynced] int _stateSequence                            │   │
│  │  [UdonSynced] byte[] _spawnRequests                         │   │
│  │  [UdonSynced] byte[] _despawnRequests                       │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │                 Parallel Array Storage (SoA)                 │   │
│  │  int[]        _prefabIds      [maxEntities]                 │   │
│  │  Vector3[]    _positions      [maxEntities]                 │   │
│  │  Quaternion[] _rotations      [maxEntities]                 │   │
│  │  Vector3[]    _velocities     [maxEntities]                 │   │
│  │  Vector3[]    _angularVelocities [maxEntities]              │   │
│  │  Vector3[]    _scales         [maxEntities]                 │   │
│  │  int[]        _ownerPlayerIds [maxEntities]                 │   │
│  │  float[]      _spawnTimes     [maxEntities]                 │   │
│  │  float[]      _lifetimes      [maxEntities]                 │   │
│  │  int[]        _flags          [maxEntities]                 │   │
│  │  int[]        _parentIds      [maxEntities]                 │   │
│  │  int[]        _tags           [maxEntities]                 │   │
│  │  int[]        _layers         [maxEntities]                 │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │                    Component Storage                         │   │
│  │  VirtualEntityComponent[][] _components [maxEntities][slots]│   │
│  │  byte[][] _componentData [maxEntities][maxDataSize]         │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │                   Visual Pool (Local Only)                   │   │
│  │  CEPool<GameObject>[] _visualPools [prefabCount]            │   │
│  │  GameObject[] _activeVisuals [maxEntities]                  │   │
│  │  bool[] _visualActive [maxEntities]                         │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │                    Entity Management                         │   │
│  │  int[] _activeList [maxEntities]  (dense)                   │   │
│  │  int[] _freeStack [maxEntities]                             │   │
│  │  int _activeCount                                           │   │
│  │  int _freeCount                                             │   │
│  │  int[] _entityVersions [maxEntities]                        │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         VirtualEntity                               │
│  (Lightweight handle - NOT a component on visual GameObject)        │
├─────────────────────────────────────────────────────────────────────┤
│  internal VirtualEntityWorld _world                                 │
│  internal int _entityId                                             │
│  internal int _version  (for stale handle detection)                │
├─────────────────────────────────────────────────────────────────────┤
│  Properties delegate to world arrays:                               │
│    position, rotation, velocity, scale, etc.                        │
│  Methods:                                                           │
│    Destroy(), GetComponent<T>(), AddComponent<T>()                  │
│  Static:                                                            │
│    Instantiate(), Destroy(), FindWithTag(), FindAll()               │
└─────────────────────────────────────────────────────────────────────┘
```

### Data Flow

```
[Client A: Spawn Request]
         │
         ▼
┌─────────────────┐     Network      ┌─────────────────┐
│   Local Client  │ ───────────────► │     Master      │
│                 │                  │                 │
│ RequestSpawn()  │                  │ ProcessSpawn()  │
│                 │                  │ UpdateState()   │
└─────────────────┘                  │ Serialize()     │
                                     │ RequestSerial() │
                                     └────────┬────────┘
                                              │
                              Network (OnDeserialization)
                                              │
         ┌────────────────────────────────────┼────────────────────────────────────┐
         ▼                                    ▼                                    ▼
┌─────────────────┐                  ┌─────────────────┐                  ┌─────────────────┐
│   Client A      │                  │   Client B      │                  │   Client C      │
│                 │                  │                 │                  │                 │
│ Deserialize()   │                  │ Deserialize()   │                  │ Deserialize()   │
│ UpdateVisuals() │                  │ UpdateVisuals() │                  │ UpdateVisuals() │
│ SpawnVisual()   │                  │ SpawnVisual()   │                  │ SpawnVisual()   │
└─────────────────┘                  └─────────────────┘                  └─────────────────┘
```

---

## Core Classes

### VirtualEntity

The primary user-facing class. Provides a Unity GameObject-like interface.

```csharp
namespace UdonSharp.CE.VirtualEntities
{
    /// <summary>
    /// Represents a networked virtual entity with Unity-like API.
    /// This is a lightweight handle that references entity data stored in VirtualEntityWorld.
    /// </summary>
    [PublicAPI]
    public class VirtualEntity : UdonSharpBehaviour
    {
        //=============================================================================
        // Internal State
        //=============================================================================

        internal VirtualEntityWorld _world;
        internal int _entityId = -1;
        internal int _version = -1;

        //=============================================================================
        // Validity
        //=============================================================================

        /// <summary>
        /// Whether this entity handle is valid and references a live entity.
        /// Always check this before accessing properties if the entity may have been destroyed.
        /// </summary>
        public bool isValid => _world != null
                            && _entityId >= 0
                            && _world.IsEntityValid(_entityId, _version);

        /// <summary>
        /// The internal entity ID. Use for advanced scenarios only.
        /// </summary>
        public int entityId => _entityId;

        //=============================================================================
        // Transform Properties (mirrors UnityEngine.Transform)
        //=============================================================================

        /// <summary>
        /// World space position of the entity.
        /// </summary>
        public Vector3 position
        {
            get => _world.GetPosition(_entityId);
            set => _world.SetPosition(_entityId, value);
        }

        /// <summary>
        /// World space rotation of the entity.
        /// </summary>
        public Quaternion rotation
        {
            get => _world.GetRotation(_entityId);
            set => _world.SetRotation(_entityId, value);
        }

        /// <summary>
        /// Local scale of the entity.
        /// </summary>
        public Vector3 localScale
        {
            get => _world.GetScale(_entityId);
            set => _world.SetScale(_entityId, value);
        }

        /// <summary>
        /// Forward direction (shorthand for rotation * Vector3.forward).
        /// </summary>
        public Vector3 forward => rotation * Vector3.forward;

        /// <summary>
        /// Right direction (shorthand for rotation * Vector3.right).
        /// </summary>
        public Vector3 right => rotation * Vector3.right;

        /// <summary>
        /// Up direction (shorthand for rotation * Vector3.up).
        /// </summary>
        public Vector3 up => rotation * Vector3.up;

        /// <summary>
        /// Euler angles representation of rotation.
        /// </summary>
        public Vector3 eulerAngles
        {
            get => rotation.eulerAngles;
            set => rotation = Quaternion.Euler(value);
        }

        //=============================================================================
        // Rigidbody-like Properties
        //=============================================================================

        /// <summary>
        /// Linear velocity in world space.
        /// </summary>
        public Vector3 velocity
        {
            get => _world.GetVelocity(_entityId);
            set => _world.SetVelocity(_entityId, value);
        }

        /// <summary>
        /// Angular velocity in radians per second.
        /// </summary>
        public Vector3 angularVelocity
        {
            get => _world.GetAngularVelocity(_entityId);
            set => _world.SetAngularVelocity(_entityId, value);
        }

        /// <summary>
        /// Whether gravity is applied to this entity during simulation.
        /// </summary>
        public bool useGravity
        {
            get => _world.GetFlag(_entityId, EntityFlags.UseGravity);
            set => _world.SetFlag(_entityId, EntityFlags.UseGravity, value);
        }

        /// <summary>
        /// Whether physics simulation is enabled for this entity.
        /// </summary>
        public bool isKinematic
        {
            get => _world.GetFlag(_entityId, EntityFlags.IsKinematic);
            set => _world.SetFlag(_entityId, EntityFlags.IsKinematic, value);
        }

        //=============================================================================
        // GameObject-like Properties
        //=============================================================================

        /// <summary>
        /// Whether this entity is active.
        /// </summary>
        public bool activeSelf
        {
            get => _world.GetFlag(_entityId, EntityFlags.Active);
            set => _world.SetActive(_entityId, value);
        }

        /// <summary>
        /// Whether this entity is active in the hierarchy (considers parent state).
        /// </summary>
        public bool activeInHierarchy => _world.IsActiveInHierarchy(_entityId);

        /// <summary>
        /// The tag assigned to this entity.
        /// </summary>
        public string tag
        {
            get => _world.GetTag(_entityId);
            set => _world.SetTag(_entityId, value);
        }

        /// <summary>
        /// The layer this entity belongs to (for filtering/collision).
        /// </summary>
        public int layer
        {
            get => _world.GetLayer(_entityId);
            set => _world.SetLayer(_entityId, value);
        }

        /// <summary>
        /// The prefab this entity was instantiated from.
        /// </summary>
        public VirtualEntityPrefab prefab => _world.GetPrefab(_entityId);

        /// <summary>
        /// The player ID that owns this entity (-1 if unowned or master-owned).
        /// </summary>
        public int ownerId => _world.GetOwnerId(_entityId);

        /// <summary>
        /// The local visual GameObject representing this entity.
        /// May be null if visual hasn't spawned yet.
        /// </summary>
        public GameObject visual => _world.GetVisual(_entityId);

        /// <summary>
        /// Time.time when this entity was spawned.
        /// </summary>
        public float spawnTime => _world.GetSpawnTime(_entityId);

        /// <summary>
        /// Remaining lifetime in seconds. 0 = infinite.
        /// </summary>
        public float remainingLifetime
        {
            get => _world.GetRemainingLifetime(_entityId);
            set => _world.SetRemainingLifetime(_entityId, value);
        }

        //=============================================================================
        // Hierarchy
        //=============================================================================

        /// <summary>
        /// Parent entity (null if root).
        /// </summary>
        public VirtualEntity parent
        {
            get => _world.GetParent(_entityId);
            set => _world.SetParent(_entityId, value);
        }

        /// <summary>
        /// Number of child entities.
        /// </summary>
        public int childCount => _world.GetChildCount(_entityId);

        /// <summary>
        /// Gets a child entity by index.
        /// </summary>
        public VirtualEntity GetChild(int index) => _world.GetChild(_entityId, index);

        /// <summary>
        /// Detaches from parent (becomes root entity).
        /// </summary>
        public void DetachFromParent() => _world.SetParent(_entityId, null);

        /// <summary>
        /// Detaches all children from this entity.
        /// </summary>
        public void DetachChildren() => _world.DetachChildren(_entityId);

        //=============================================================================
        // Static Instantiate Methods (mirrors UnityEngine.Object.Instantiate)
        //=============================================================================

        /// <summary>
        /// Instantiates a new virtual entity.
        /// </summary>
        /// <param name="prefab">The prefab to instantiate.</param>
        /// <returns>The spawned entity, or null if spawn failed.</returns>
        public static VirtualEntity Instantiate(VirtualEntityPrefab prefab)
        {
            return VirtualEntityWorld.Instance.Spawn(prefab, Vector3.zero, Quaternion.identity);
        }

        /// <summary>
        /// Instantiates a new virtual entity at the specified position and rotation.
        /// </summary>
        public static VirtualEntity Instantiate(VirtualEntityPrefab prefab, Vector3 position, Quaternion rotation)
        {
            return VirtualEntityWorld.Instance.Spawn(prefab, position, rotation);
        }

        /// <summary>
        /// Instantiates a new virtual entity as a child of another entity.
        /// </summary>
        public static VirtualEntity Instantiate(VirtualEntityPrefab prefab, VirtualEntity parent)
        {
            var entity = VirtualEntityWorld.Instance.Spawn(prefab, Vector3.zero, Quaternion.identity);
            if (entity != null)
                entity.parent = parent;
            return entity;
        }

        /// <summary>
        /// Instantiates a new virtual entity at position/rotation as a child.
        /// </summary>
        public static VirtualEntity Instantiate(VirtualEntityPrefab prefab, Vector3 position, Quaternion rotation, VirtualEntity parent)
        {
            var entity = VirtualEntityWorld.Instance.Spawn(prefab, position, rotation);
            if (entity != null)
                entity.parent = parent;
            return entity;
        }

        //=============================================================================
        // Destroy Methods (mirrors UnityEngine.Object.Destroy)
        //=============================================================================

        /// <summary>
        /// Destroys this entity immediately.
        /// </summary>
        public void Destroy()
        {
            _world.Despawn(_entityId);
        }

        /// <summary>
        /// Destroys this entity after a delay.
        /// </summary>
        /// <param name="delay">Time in seconds before destruction.</param>
        public void Destroy(float delay)
        {
            _world.DespawnDelayed(_entityId, delay);
        }

        /// <summary>
        /// Destroys an entity immediately (static version).
        /// </summary>
        public static void Destroy(VirtualEntity entity)
        {
            if (entity != null && entity.isValid)
                entity.Destroy();
        }

        /// <summary>
        /// Destroys an entity after a delay (static version).
        /// </summary>
        public static void Destroy(VirtualEntity entity, float delay)
        {
            if (entity != null && entity.isValid)
                entity.Destroy(delay);
        }

        //=============================================================================
        // Component Access (mirrors GameObject.GetComponent)
        //=============================================================================

        /// <summary>
        /// Gets a component of the specified type attached to this entity.
        /// </summary>
        /// <typeparam name="T">Component type (must derive from VirtualEntityComponent).</typeparam>
        /// <returns>The component, or null if not found.</returns>
        public T GetComponent<T>() where T : VirtualEntityComponent
        {
            return _world.GetComponent<T>(_entityId);
        }

        /// <summary>
        /// Gets a component by type ID.
        /// </summary>
        public VirtualEntityComponent GetComponent(int componentTypeId)
        {
            return _world.GetComponent(_entityId, componentTypeId);
        }

        /// <summary>
        /// Adds a component of the specified type to this entity.
        /// </summary>
        public T AddComponent<T>() where T : VirtualEntityComponent
        {
            return _world.AddComponent<T>(_entityId);
        }

        /// <summary>
        /// Removes a component from this entity.
        /// </summary>
        public bool RemoveComponent<T>() where T : VirtualEntityComponent
        {
            return _world.RemoveComponent<T>(_entityId);
        }

        /// <summary>
        /// Checks if this entity has a component of the specified type.
        /// </summary>
        public bool HasComponent<T>() where T : VirtualEntityComponent
        {
            return _world.HasComponent<T>(_entityId);
        }

        /// <summary>
        /// Gets all components attached to this entity.
        /// </summary>
        /// <param name="results">Array to fill with components.</param>
        /// <returns>Number of components found.</returns>
        public int GetComponents(VirtualEntityComponent[] results)
        {
            return _world.GetComponents(_entityId, results);
        }

        //=============================================================================
        // Physics Helpers
        //=============================================================================

        /// <summary>
        /// Adds a force to the entity (requires physics simulation enabled).
        /// </summary>
        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            _world.AddForce(_entityId, force, mode);
        }

        /// <summary>
        /// Adds a torque to the entity.
        /// </summary>
        public void AddTorque(Vector3 torque, ForceMode mode = ForceMode.Force)
        {
            _world.AddTorque(_entityId, torque, mode);
        }

        /// <summary>
        /// Adds force at a specific world position.
        /// </summary>
        public void AddForceAtPosition(Vector3 force, Vector3 position, ForceMode mode = ForceMode.Force)
        {
            _world.AddForceAtPosition(_entityId, force, position, mode);
        }

        /// <summary>
        /// Adds an explosion force affecting this entity.
        /// </summary>
        public void AddExplosionForce(float force, Vector3 explosionPosition, float radius, float upwardsModifier = 0f)
        {
            _world.AddExplosionForce(_entityId, force, explosionPosition, radius, upwardsModifier);
        }

        //=============================================================================
        // Transform Helpers
        //=============================================================================

        /// <summary>
        /// Moves position towards target.
        /// </summary>
        public void MoveTowards(Vector3 target, float maxDistanceDelta)
        {
            position = Vector3.MoveTowards(position, target, maxDistanceDelta);
        }

        /// <summary>
        /// Rotates towards target direction.
        /// </summary>
        public void RotateTowards(Quaternion target, float maxDegreesDelta)
        {
            rotation = Quaternion.RotateTowards(rotation, target, maxDegreesDelta);
        }

        /// <summary>
        /// Looks at a target position.
        /// </summary>
        public void LookAt(Vector3 target)
        {
            rotation = Quaternion.LookRotation(target - position);
        }

        /// <summary>
        /// Looks at a target position with specified up vector.
        /// </summary>
        public void LookAt(Vector3 target, Vector3 worldUp)
        {
            rotation = Quaternion.LookRotation(target - position, worldUp);
        }

        /// <summary>
        /// Translates the entity in world space.
        /// </summary>
        public void Translate(Vector3 translation, Space relativeTo = Space.Self)
        {
            if (relativeTo == Space.World)
                position += translation;
            else
                position += rotation * translation;
        }

        /// <summary>
        /// Rotates the entity.
        /// </summary>
        public void Rotate(Vector3 eulerAngles, Space relativeTo = Space.Self)
        {
            if (relativeTo == Space.World)
                rotation = Quaternion.Euler(eulerAngles) * rotation;
            else
                rotation = rotation * Quaternion.Euler(eulerAngles);
        }

        //=============================================================================
        // Find Methods (mirrors GameObject.Find*)
        //=============================================================================

        /// <summary>
        /// Finds all entities with the specified tag.
        /// </summary>
        /// <param name="tag">Tag to search for.</param>
        /// <param name="results">Array to fill with results.</param>
        /// <returns>Number of entities found.</returns>
        public static int FindWithTag(string tag, VirtualEntity[] results)
        {
            return VirtualEntityWorld.Instance.FindWithTag(tag, results);
        }

        /// <summary>
        /// Finds all entities of a specific prefab type.
        /// </summary>
        public static int FindAllOfPrefab(VirtualEntityPrefab prefab, VirtualEntity[] results)
        {
            return VirtualEntityWorld.Instance.FindAllOfPrefab(prefab, results);
        }

        /// <summary>
        /// Finds the nearest entity to a position.
        /// </summary>
        public static VirtualEntity FindNearest(Vector3 position, float maxDistance = float.MaxValue)
        {
            return VirtualEntityWorld.Instance.FindNearest(position, maxDistance);
        }

        /// <summary>
        /// Finds all entities within a radius.
        /// </summary>
        public static int FindInRadius(Vector3 center, float radius, VirtualEntity[] results)
        {
            return VirtualEntityWorld.Instance.FindInRadius(center, radius, results);
        }

        /// <summary>
        /// Finds all entities owned by a specific player.
        /// </summary>
        public static int FindByOwner(int playerId, VirtualEntity[] results)
        {
            return VirtualEntityWorld.Instance.FindByOwner(playerId, results);
        }

        //=============================================================================
        // Comparison Methods
        //=============================================================================

        /// <summary>
        /// Compares two entity handles for equality.
        /// </summary>
        public bool Equals(VirtualEntity other)
        {
            if (other == null) return false;
            return _world == other._world
                && _entityId == other._entityId
                && _version == other._version;
        }

        /// <summary>
        /// Returns a hash code for this entity.
        /// </summary>
        public override int GetHashCode()
        {
            return (_entityId * 397) ^ _version;
        }

        //=============================================================================
        // Debug
        //=============================================================================

        /// <summary>
        /// Returns a string representation for debugging.
        /// </summary>
        public override string ToString()
        {
            if (!isValid) return $"VirtualEntity(Invalid)";
            var p = prefab;
            string prefabName = p != null ? p.name : "Unknown";
            return $"VirtualEntity({_entityId}:{prefabName})";
        }
    }
}
```

### VirtualEntityWorld

The singleton coordinator managing all virtual entities.

```csharp
namespace UdonSharp.CE.VirtualEntities
{
    /// <summary>
    /// Central coordinator for all virtual entities in the world.
    /// Manages entity state, networking, and local visual representations.
    /// </summary>
    /// <remarks>
    /// Place exactly one VirtualEntityWorld in your scene. It will automatically
    /// register as the singleton instance.
    ///
    /// The master client owns the authoritative entity state. Non-master clients
    /// send spawn/despawn requests which the master processes and broadcasts.
    /// </remarks>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class VirtualEntityWorld : UdonSharpBehaviour
    {
        //=============================================================================
        // Singleton
        //=============================================================================

        /// <summary>
        /// The singleton instance. Set automatically on Start().
        /// </summary>
        public static VirtualEntityWorld Instance { get; private set; }

        //=============================================================================
        // Configuration (Inspector)
        //=============================================================================

        [Header("Capacity")]
        [Tooltip("Maximum number of simultaneous virtual entities.")]
        [SerializeField] private int _maxEntities = 128;

        [Tooltip("Maximum number of registered prefab types.")]
        [SerializeField] private int _maxPrefabTypes = 32;

        [Tooltip("Maximum component types per entity.")]
        [SerializeField] private int _maxComponentsPerEntity = 8;

        [Header("Prefabs")]
        [Tooltip("Registered virtual entity prefabs. Index = prefab ID.")]
        [SerializeField] private VirtualEntityPrefab[] _prefabs;

        [Header("Physics")]
        [Tooltip("Enable physics simulation for entities with simulatePhysics=true.")]
        [SerializeField] private bool _enablePhysics = true;

        [Tooltip("Gravity applied to entities with useGravity=true.")]
        [SerializeField] private Vector3 _gravity = new Vector3(0, -9.81f, 0);

        [Tooltip("Fixed timestep for physics simulation.")]
        [SerializeField] private float _fixedDeltaTime = 0.02f;

        [Header("Networking")]
        [Tooltip("How often to broadcast state updates (Hz).")]
        [SerializeField] private float _syncRate = 20f;

        [Tooltip("Maximum entities per sync packet.")]
        [SerializeField] private int _maxEntitiesPerPacket = 16;

        [Tooltip("Enable state compression.")]
        [SerializeField] private bool _compressState = true;

        [Header("Interpolation")]
        [Tooltip("Interpolation mode for visual smoothing.")]
        [SerializeField] private InterpolationMode _interpolationMode = InterpolationMode.Linear;

        [Tooltip("Interpolation delay in seconds.")]
        [SerializeField] private float _interpolationDelay = 0.1f;

        [Header("Visual Pooling")]
        [Tooltip("Pre-warm visual pools on Start.")]
        [SerializeField] private bool _prewarmPools = true;

        [Tooltip("Pool size multiplier (entities * multiplier = pool size per prefab).")]
        [SerializeField] private float _poolSizeMultiplier = 0.5f;

        [Header("World Bounds (for position compression)")]
        [SerializeField] private Vector3 _worldMin = new Vector3(-500, -50, -500);
        [SerializeField] private Vector3 _worldMax = new Vector3(500, 200, 500);

        [Header("Tags")]
        [Tooltip("Registered tag strings. Index = tag ID.")]
        [SerializeField] private string[] _tags;

        //=============================================================================
        // Synced State
        //=============================================================================

        /// <summary>
        /// Compressed entity state data.
        /// </summary>
        [UdonSynced] private byte[] _entityState;

        /// <summary>
        /// State sequence number for change detection.
        /// </summary>
        [UdonSynced] private int _stateSequence;

        /// <summary>
        /// Pending spawn requests from non-master clients.
        /// Format: [count][request1][request2]...
        /// </summary>
        [UdonSynced] private byte[] _spawnRequests;

        /// <summary>
        /// Spawn request sequence for change detection.
        /// </summary>
        [UdonSynced] private int _spawnRequestSequence;

        //=============================================================================
        // Entity Storage (Parallel Arrays)
        //=============================================================================

        // Core state
        private int[] _prefabIds;           // 0 = inactive slot
        private Vector3[] _positions;
        private Quaternion[] _rotations;
        private Vector3[] _scales;
        private Vector3[] _velocities;
        private Vector3[] _angularVelocities;
        private int[] _ownerPlayerIds;
        private float[] _spawnTimes;
        private float[] _lifetimes;         // Remaining lifetime, 0 = infinite
        private int[] _flags;               // Bitfield (EntityFlags)
        private int[] _parentIds;           // -1 = no parent
        private int[] _tagIds;              // Index into _tags array
        private int[] _layers;
        private int[] _versions;            // Incremented on reuse

        // Component storage
        private VirtualEntityComponent[][] _components;
        private int[] _componentMasks;      // Bitfield of attached component types

        // Management
        private int[] _activeList;          // Dense list of active entity IDs
        private int _activeCount;
        private int[] _freeStack;           // Stack of free slot indices
        private int _freeCount;

        // Entity handles (pooled)
        private VirtualEntity[] _entityHandles;

        //=============================================================================
        // Visual Pool
        //=============================================================================

        private CEPool<GameObject>[] _visualPools;
        private GameObject[] _activeVisuals;
        private bool[] _visualActive;

        //=============================================================================
        // Interpolation State
        //=============================================================================

        private Vector3[] _previousPositions;
        private Quaternion[] _previousRotations;
        private Vector3[] _targetPositions;
        private Quaternion[] _targetRotations;
        private float _lastSyncTime;
        private float _syncInterval;

        //=============================================================================
        // Network State
        //=============================================================================

        private int _lastProcessedSequence = -1;
        private int _lastProcessedSpawnSequence = -1;
        private float _lastSyncBroadcast;
        private int _syncCursor;            // Round-robin cursor for partial updates

        // Pending spawn/despawn for non-master clients
        private int[] _pendingSpawnPrefabs;
        private Vector3[] _pendingSpawnPositions;
        private Quaternion[] _pendingSpawnRotations;
        private int _pendingSpawnCount;

        //=============================================================================
        // Compressor
        //=============================================================================

        private VirtualEntityCompressor _compressor;

        //=============================================================================
        // Properties
        //=============================================================================

        /// <summary>
        /// Maximum entity capacity.
        /// </summary>
        public int maxEntities => _maxEntities;

        /// <summary>
        /// Current number of active entities.
        /// </summary>
        public int activeCount => _activeCount;

        /// <summary>
        /// Number of available entity slots.
        /// </summary>
        public int availableSlots => _freeCount;

        /// <summary>
        /// Whether this client is the master (authority).
        /// </summary>
        public bool isMaster => Networking.IsMaster;

        /// <summary>
        /// Registered prefabs array.
        /// </summary>
        public VirtualEntityPrefab[] prefabs => _prefabs;

        /// <summary>
        /// World bounds minimum (for compression).
        /// </summary>
        public Vector3 worldMin => _worldMin;

        /// <summary>
        /// World bounds maximum (for compression).
        /// </summary>
        public Vector3 worldMax => _worldMax;

        /// <summary>
        /// Dense array of active entity IDs. Use with activeCount for iteration.
        /// </summary>
        public int[] activeEntities => _activeList;

        //=============================================================================
        // Lifecycle
        //=============================================================================

        private void Start();
        private void FixedUpdate();
        private void Update();
        private void LateUpdate();

        public override void OnDeserialization();
        public override void OnPlayerJoined(VRCPlayerApi player);
        public override void OnPlayerLeft(VRCPlayerApi player);

        //=============================================================================
        // Spawn / Despawn API
        //=============================================================================

        /// <summary>
        /// Spawns a virtual entity. On non-master clients, this sends a request to master.
        /// </summary>
        public VirtualEntity Spawn(VirtualEntityPrefab prefab, Vector3 position, Quaternion rotation);

        /// <summary>
        /// Spawns a virtual entity with a specific owner.
        /// </summary>
        public VirtualEntity Spawn(VirtualEntityPrefab prefab, Vector3 position, Quaternion rotation, int ownerPlayerId);

        /// <summary>
        /// Spawns a virtual entity by prefab index.
        /// </summary>
        public VirtualEntity SpawnByIndex(int prefabIndex, Vector3 position, Quaternion rotation);

        /// <summary>
        /// Despawns an entity immediately.
        /// </summary>
        public void Despawn(int entityId);

        /// <summary>
        /// Despawns an entity after a delay.
        /// </summary>
        public void DespawnDelayed(int entityId, float delay);

        /// <summary>
        /// Despawns all entities.
        /// </summary>
        public void DespawnAll();

        /// <summary>
        /// Despawns all entities of a specific prefab type.
        /// </summary>
        public void DespawnAllOfPrefab(VirtualEntityPrefab prefab);

        /// <summary>
        /// Despawns all entities owned by a player.
        /// </summary>
        public void DespawnByOwner(int playerId);

        //=============================================================================
        // Entity State Accessors
        //=============================================================================

        public bool IsEntityValid(int entityId, int version);
        public bool IsActiveInHierarchy(int entityId);

        // Getters
        public Vector3 GetPosition(int entityId);
        public Quaternion GetRotation(int entityId);
        public Vector3 GetScale(int entityId);
        public Vector3 GetVelocity(int entityId);
        public Vector3 GetAngularVelocity(int entityId);
        public int GetOwnerId(int entityId);
        public float GetSpawnTime(int entityId);
        public float GetRemainingLifetime(int entityId);
        public bool GetFlag(int entityId, EntityFlags flag);
        public string GetTag(int entityId);
        public int GetLayer(int entityId);
        public VirtualEntityPrefab GetPrefab(int entityId);
        public GameObject GetVisual(int entityId);
        public VirtualEntity GetParent(int entityId);
        public int GetChildCount(int entityId);
        public VirtualEntity GetChild(int entityId, int index);

        // Setters
        public void SetPosition(int entityId, Vector3 value);
        public void SetRotation(int entityId, Quaternion value);
        public void SetScale(int entityId, Vector3 value);
        public void SetVelocity(int entityId, Vector3 value);
        public void SetAngularVelocity(int entityId, Vector3 value);
        public void SetRemainingLifetime(int entityId, float value);
        public void SetFlag(int entityId, EntityFlags flag, bool value);
        public void SetActive(int entityId, bool value);
        public void SetTag(int entityId, string tag);
        public void SetLayer(int entityId, int layer);
        public void SetParent(int entityId, VirtualEntity parent);
        public void DetachChildren(int entityId);

        //=============================================================================
        // Component Management
        //=============================================================================

        public T GetComponent<T>(int entityId) where T : VirtualEntityComponent;
        public VirtualEntityComponent GetComponent(int entityId, int componentTypeId);
        public T AddComponent<T>(int entityId) where T : VirtualEntityComponent;
        public bool RemoveComponent<T>(int entityId) where T : VirtualEntityComponent;
        public bool HasComponent<T>(int entityId) where T : VirtualEntityComponent;
        public int GetComponents(int entityId, VirtualEntityComponent[] results);

        //=============================================================================
        // Physics
        //=============================================================================

        public void AddForce(int entityId, Vector3 force, ForceMode mode);
        public void AddTorque(int entityId, Vector3 torque, ForceMode mode);
        public void AddForceAtPosition(int entityId, Vector3 force, Vector3 position, ForceMode mode);
        public void AddExplosionForce(int entityId, float force, Vector3 position, float radius, float upwardsModifier);

        //=============================================================================
        // Find Methods
        //=============================================================================

        public int FindWithTag(string tag, VirtualEntity[] results);
        public int FindAllOfPrefab(VirtualEntityPrefab prefab, VirtualEntity[] results);
        public VirtualEntity FindNearest(Vector3 position, float maxDistance);
        public int FindInRadius(Vector3 center, float radius, VirtualEntity[] results);
        public int FindByOwner(int playerId, VirtualEntity[] results);
        public VirtualEntity GetEntity(int entityId);

        //=============================================================================
        // Iteration
        //=============================================================================

        /// <summary>
        /// Iterates all active entities efficiently.
        /// </summary>
        /// <example>
        /// int[] active = world.activeEntities;
        /// int count = world.activeCount;
        /// for (int i = 0; i < count; i++)
        /// {
        ///     int entityId = active[i];
        ///     Vector3 pos = world.GetPosition(entityId);
        /// }
        /// </example>

        //=============================================================================
        // Internal Methods
        //=============================================================================

        private int AllocateEntitySlot();
        private void FreeEntitySlot(int entityId);
        private void SimulatePhysics(float dt);
        private void UpdateLifetimes(float dt);
        private void UpdateVisuals();
        private void InterpolateVisuals(float t);
        private void SpawnVisual(int entityId);
        private void DespawnVisual(int entityId);
        private void BroadcastState();
        private void ProcessReceivedState();
        private void ProcessSpawnRequests();
        private void SendSpawnRequest(int prefabIndex, Vector3 position, Quaternion rotation);
    }
}
```

### VirtualEntityPrefab

ScriptableObject defining entity configuration.

```csharp
namespace UdonSharp.CE.VirtualEntities
{
    /// <summary>
    /// Defines a virtual entity type with its visual, physics, and networking settings.
    /// </summary>
    [PublicAPI]
    [CreateAssetMenu(fileName = "VirtualEntityPrefab", menuName = "CE/Virtual Entity Prefab")]
    public class VirtualEntityPrefab : ScriptableObject
    {
        //=============================================================================
        // Identification
        //=============================================================================

        [Header("Identification")]
        [Tooltip("Unique identifier for this prefab type.")]
        public string prefabId;

        [Tooltip("Display name for debugging.")]
        public string displayName;

        //=============================================================================
        // Visual
        //=============================================================================

        [Header("Visual Representation")]
        [Tooltip("The GameObject to instantiate locally for visual representation.")]
        public GameObject visualPrefab;

        [Tooltip("Offset from entity position for visual placement.")]
        public Vector3 visualOffset = Vector3.zero;

        [Tooltip("Rotation offset for visual.")]
        public Vector3 visualRotationOffset = Vector3.zero;

        [Tooltip("Scale multiplier for visual.")]
        public Vector3 visualScale = Vector3.one;

        [Tooltip("Whether to sync scale across network.")]
        public bool syncScale = false;

        //=============================================================================
        // Physics
        //=============================================================================

        [Header("Physics Simulation")]
        [Tooltip("Whether this entity undergoes physics simulation.")]
        public bool simulatePhysics = true;

        [Tooltip("Whether gravity affects this entity.")]
        public bool useGravity = false;

        [Tooltip("Linear drag coefficient.")]
        [Range(0f, 10f)]
        public float drag = 0f;

        [Tooltip("Angular drag coefficient.")]
        [Range(0f, 10f)]
        public float angularDrag = 0.05f;

        [Tooltip("Mass for force calculations.")]
        public float mass = 1f;

        [Tooltip("Maximum velocity magnitude.")]
        public float maxVelocity = 100f;

        [Tooltip("Maximum angular velocity magnitude.")]
        public float maxAngularVelocity = 50f;

        //=============================================================================
        // Collision
        //=============================================================================

        [Header("Collision")]
        [Tooltip("Collision shape for overlap tests.")]
        public CollisionShape collisionShape = CollisionShape.Sphere;

        [Tooltip("Collision radius (for sphere) or half-extents (for box).")]
        public float collisionRadius = 0.5f;

        [Tooltip("Box half-extents (if using Box shape).")]
        public Vector3 boxHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        [Tooltip("Collision layer mask.")]
        public LayerMask collisionMask = -1;

        [Tooltip("Whether to trigger OnCollision callbacks.")]
        public bool enableCollisionCallbacks = true;

        [Tooltip("Destroy entity on collision.")]
        public bool destroyOnCollision = false;

        //=============================================================================
        // Lifetime
        //=============================================================================

        [Header("Lifetime")]
        [Tooltip("Default lifetime in seconds. 0 = infinite.")]
        public float defaultLifetime = 0f;

        [Tooltip("Fade out visual before destruction.")]
        public bool fadeOnDestroy = false;

        [Tooltip("Fade duration in seconds.")]
        public float fadeDuration = 0.5f;

        //=============================================================================
        // Networking
        //=============================================================================

        [Header("Networking")]
        [Tooltip("Sync priority (lower = more frequent updates).")]
        [Range(0, 255)]
        public int syncPriority = 100;

        [Tooltip("Position quantization precision in meters.")]
        public float positionPrecision = 0.01f;

        [Tooltip("Velocity quantization precision.")]
        public float velocityPrecision = 0.1f;

        [Tooltip("Whether to sync velocity (disable for stationary objects).")]
        public bool syncVelocity = true;

        [Tooltip("Whether to sync angular velocity.")]
        public bool syncAngularVelocity = false;

        [Tooltip("Interpolation mode for this entity type.")]
        public InterpolationMode interpolation = InterpolationMode.Linear;

        [Tooltip("Extrapolation time for prediction.")]
        public float extrapolationTime = 0.1f;

        //=============================================================================
        // Components
        //=============================================================================

        [Header("Default Components")]
        [Tooltip("Component types to add automatically on spawn.")]
        public VirtualEntityComponentDefinition[] defaultComponents;

        //=============================================================================
        // Audio
        //=============================================================================

        [Header("Audio")]
        [Tooltip("Sound to play on spawn.")]
        public AudioClip spawnSound;

        [Tooltip("Sound to play on destroy.")]
        public AudioClip destroySound;

        [Tooltip("Looping sound while active.")]
        public AudioClip loopSound;

        //=============================================================================
        // Effects
        //=============================================================================

        [Header("Effects")]
        [Tooltip("Particle system to spawn on creation.")]
        public GameObject spawnEffect;

        [Tooltip("Particle system to spawn on destruction.")]
        public GameObject destroyEffect;

        [Tooltip("Trail renderer settings.")]
        public bool enableTrail = false;
        public float trailTime = 0.5f;
        public float trailWidth = 0.1f;
        public Gradient trailColor;

        //=============================================================================
        // Methods
        //=============================================================================

        /// <summary>
        /// Gets the prefab index in the world's prefab array.
        /// </summary>
        public int GetPrefabIndex(VirtualEntityWorld world)
        {
            var prefabs = world.prefabs;
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (prefabs[i] == this)
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// Collision shape types.
    /// </summary>
    public enum CollisionShape
    {
        None,
        Sphere,
        Box,
        Capsule
    }

    /// <summary>
    /// Interpolation modes for visual smoothing.
    /// </summary>
    public enum InterpolationMode
    {
        None,
        Linear,
        Smooth,      // SmoothDamp
        Predictive   // Extrapolate based on velocity
    }

    /// <summary>
    /// Definition for a default component on a prefab.
    /// </summary>
    [System.Serializable]
    public class VirtualEntityComponentDefinition
    {
        public int componentTypeId;
        public byte[] initialData;
    }
}
```

### VirtualEntityComponent

Base class for custom entity behaviours.

```csharp
namespace UdonSharp.CE.VirtualEntities
{
    /// <summary>
    /// Base class for virtual entity components.
    /// Provides a MonoBehaviour-like interface for entity-specific logic.
    /// </summary>
    /// <remarks>
    /// Components are not Unity components - they're lightweight data containers
    /// with optional serialization for network sync.
    ///
    /// Override OnSerialize/OnDeserialize to sync custom data.
    /// </remarks>
    [PublicAPI]
    public abstract class VirtualEntityComponent : UdonSharpBehaviour
    {
        //=============================================================================
        // References
        //=============================================================================

        /// <summary>
        /// The entity this component is attached to.
        /// </summary>
        public VirtualEntity entity { get; internal set; }

        /// <summary>
        /// The world managing this entity.
        /// </summary>
        public VirtualEntityWorld world => entity != null ? entity._world : null;

        /// <summary>
        /// The entity ID.
        /// </summary>
        public int entityId => entity != null ? entity._entityId : -1;

        /// <summary>
        /// Whether this component is on a valid entity.
        /// </summary>
        public bool isValid => entity != null && entity.isValid;

        //=============================================================================
        // Component Type
        //=============================================================================

        /// <summary>
        /// Unique type ID for this component type.
        /// Override in derived classes.
        /// </summary>
        public virtual int componentTypeId => 0;

        /// <summary>
        /// Size in bytes of serialized component data.
        /// Override if component has networked state.
        /// </summary>
        public virtual int serializedSize => 0;

        //=============================================================================
        // Lifecycle Callbacks
        //=============================================================================

        /// <summary>
        /// Called when the component is added to an entity.
        /// </summary>
        public virtual void OnAttach() { }

        /// <summary>
        /// Called when the component is removed from an entity.
        /// </summary>
        public virtual void OnDetach() { }

        /// <summary>
        /// Called when the entity is spawned.
        /// </summary>
        public virtual void OnSpawn() { }

        /// <summary>
        /// Called when the entity is about to be despawned.
        /// </summary>
        public virtual void OnDespawn() { }

        /// <summary>
        /// Called when the entity becomes active.
        /// </summary>
        public virtual void OnEnable() { }

        /// <summary>
        /// Called when the entity becomes inactive.
        /// </summary>
        public virtual void OnDisable() { }

        //=============================================================================
        // Update Callbacks
        //=============================================================================

        /// <summary>
        /// Called every frame for active entities.
        /// </summary>
        public virtual void OnUpdate() { }

        /// <summary>
        /// Called at fixed timestep for physics-enabled entities.
        /// </summary>
        public virtual void OnFixedUpdate() { }

        /// <summary>
        /// Called after all updates, before rendering.
        /// </summary>
        public virtual void OnLateUpdate() { }

        //=============================================================================
        // Physics Callbacks
        //=============================================================================

        /// <summary>
        /// Called when this entity collides with another.
        /// </summary>
        /// <param name="other">The other entity.</param>
        /// <param name="point">World-space collision point.</param>
        /// <param name="normal">Collision normal.</param>
        public virtual void OnCollision(VirtualEntity other, Vector3 point, Vector3 normal) { }

        /// <summary>
        /// Called when this entity enters a trigger zone.
        /// </summary>
        public virtual void OnTriggerEnter(VirtualEntity other) { }

        /// <summary>
        /// Called when this entity exits a trigger zone.
        /// </summary>
        public virtual void OnTriggerExit(VirtualEntity other) { }

        //=============================================================================
        // Network Callbacks
        //=============================================================================

        /// <summary>
        /// Called on the owner when ownership is gained.
        /// </summary>
        public virtual void OnOwnershipGained() { }

        /// <summary>
        /// Called on the previous owner when ownership is lost.
        /// </summary>
        public virtual void OnOwnershipLost() { }

        /// <summary>
        /// Called when entity state is received from network.
        /// </summary>
        public virtual void OnNetworkSync() { }

        //=============================================================================
        // Serialization
        //=============================================================================

        /// <summary>
        /// Serializes component state for network transmission.
        /// Called on the master client.
        /// </summary>
        /// <param name="data">Buffer to write to.</param>
        /// <param name="offset">Current write offset (update after writing).</param>
        public virtual void OnSerialize(byte[] data, ref int offset) { }

        /// <summary>
        /// Deserializes component state from network data.
        /// Called on non-master clients.
        /// </summary>
        /// <param name="data">Buffer to read from.</param>
        /// <param name="offset">Current read offset (update after reading).</param>
        public virtual void OnDeserialize(byte[] data, ref int offset) { }

        //=============================================================================
        // Convenience Accessors
        //=============================================================================

        /// <summary>
        /// Entity position (shorthand).
        /// </summary>
        protected Vector3 position
        {
            get => entity.position;
            set => entity.position = value;
        }

        /// <summary>
        /// Entity rotation (shorthand).
        /// </summary>
        protected Quaternion rotation
        {
            get => entity.rotation;
            set => entity.rotation = value;
        }

        /// <summary>
        /// Entity velocity (shorthand).
        /// </summary>
        protected Vector3 velocity
        {
            get => entity.velocity;
            set => entity.velocity = value;
        }

        /// <summary>
        /// Whether this client owns the entity.
        /// </summary>
        protected bool isOwner
        {
            get
            {
                var local = Networking.LocalPlayer;
                return local != null && entity.ownerId == local.playerId;
            }
        }

        /// <summary>
        /// Whether this client is the master.
        /// </summary>
        protected bool isMaster => Networking.IsMaster;
    }
}
```

### EntityFlags

Bitfield for entity state flags.

```csharp
namespace UdonSharp.CE.VirtualEntities
{
    /// <summary>
    /// Bitfield flags for entity state.
    /// </summary>
    [PublicAPI]
    [System.Flags]
    public enum EntityFlags
    {
        None = 0,

        /// <summary>
        /// Entity is active and should be simulated/rendered.
        /// </summary>
        Active = 1 << 0,

        /// <summary>
        /// Gravity affects this entity.
        /// </summary>
        UseGravity = 1 << 1,

        /// <summary>
        /// Entity is kinematic (no physics simulation).
        /// </summary>
        IsKinematic = 1 << 2,

        /// <summary>
        /// Entity has been modified and needs sync.
        /// </summary>
        Dirty = 1 << 3,

        /// <summary>
        /// Entity is pending destruction.
        /// </summary>
        PendingDestroy = 1 << 4,

        /// <summary>
        /// Entity was spawned locally (awaiting master confirmation).
        /// </summary>
        LocalSpawn = 1 << 5,

        /// <summary>
        /// Entity is a trigger (no collision response).
        /// </summary>
        IsTrigger = 1 << 6,

        /// <summary>
        /// Entity should be included in collision queries.
        /// </summary>
        CollisionEnabled = 1 << 7,

        /// <summary>
        /// Entity visual should be hidden.
        /// </summary>
        VisualHidden = 1 << 8,

        /// <summary>
        /// Entity is interpolating to target state.
        /// </summary>
        Interpolating = 1 << 9,

        /// <summary>
        /// Entity has custom component data to sync.
        /// </summary>
        HasComponentData = 1 << 10,

        /// <summary>
        /// Entity scale should be synced.
        /// </summary>
        SyncScale = 1 << 11,

        /// <summary>
        /// Entity angular velocity should be synced.
        /// </summary>
        SyncAngularVelocity = 1 << 12,
    }
}
```

### VirtualEntityCompressor

Handles state serialization/deserialization.

```csharp
namespace UdonSharp.CE.VirtualEntities
{
    /// <summary>
    /// Compresses and decompresses virtual entity state for network transmission.
    /// </summary>
    /// <remarks>
    /// Uses quantization techniques similar to CE.NetPhysics.StateCompressor:
    /// - Position: 16-bit per axis within world bounds
    /// - Rotation: Smallest-three quaternion (6 bytes)
    /// - Velocity: 12-bit per axis with precision scaling
    ///
    /// Base entity state: 21-24 bytes per entity
    /// - Prefab ID: 1 byte (max 255 prefab types)
    /// - Entity ID: 2 bytes (max 65535 entities)
    /// - Position: 6 bytes
    /// - Rotation: 6 bytes
    /// - Velocity: 5 bytes (optional based on prefab)
    /// - Flags: 2 bytes
    /// - Owner: 1 byte
    /// - Lifetime: 2 bytes (optional)
    ///
    /// Component data is appended per-entity based on component masks.
    /// </remarks>
    [PublicAPI]
    public class VirtualEntityCompressor
    {
        //=============================================================================
        // Configuration
        //=============================================================================

        /// <summary>
        /// World bounds for position quantization.
        /// </summary>
        public Vector3 WorldMin = new Vector3(-500, -50, -500);
        public Vector3 WorldMax = new Vector3(500, 200, 500);

        /// <summary>
        /// Position precision hint.
        /// </summary>
        public float PositionPrecision = 0.01f;

        /// <summary>
        /// Velocity precision (units per second).
        /// </summary>
        public float VelocityPrecision = 0.1f;

        //=============================================================================
        // Constants
        //=============================================================================

        /// <summary>
        /// Header size: sequence(4) + count(2) + timestamp(4) = 10 bytes
        /// </summary>
        public const int HeaderBytes = 10;

        /// <summary>
        /// Minimum bytes per entity (without optional fields).
        /// </summary>
        public const int MinBytesPerEntity = 18;

        /// <summary>
        /// Maximum bytes per entity (with all optional fields).
        /// </summary>
        public const int MaxBytesPerEntity = 32;

        //=============================================================================
        // Methods
        //=============================================================================

        /// <summary>
        /// Compresses entity state to bytes.
        /// </summary>
        public byte[] Compress(VirtualEntityWorld world, int[] entityIds, int count);

        /// <summary>
        /// Decompresses bytes to entity state.
        /// </summary>
        public void Decompress(byte[] data, VirtualEntityWorld world);

        /// <summary>
        /// Estimates compressed size for a set of entities.
        /// </summary>
        public int EstimateSize(VirtualEntityWorld world, int[] entityIds, int count);

        //=============================================================================
        // Internal Encoding
        //=============================================================================

        private void WritePosition(byte[] data, ref int offset, Vector3 position);
        private Vector3 ReadPosition(byte[] data, ref int offset);

        private void WriteRotation(byte[] data, ref int offset, Quaternion rotation);
        private Quaternion ReadRotation(byte[] data, ref int offset);

        private void WriteVelocity(byte[] data, ref int offset, Vector3 velocity);
        private Vector3 ReadVelocity(byte[] data, ref int offset);

        private void WriteFlags(byte[] data, ref int offset, int flags);
        private int ReadFlags(byte[] data, ref int offset);
    }
}
```

---

## Example Components

### Projectile

```csharp
namespace UdonSharp.CE.VirtualEntities.Components
{
    /// <summary>
    /// Projectile component with damage and impact handling.
    /// </summary>
    [PublicAPI]
    public class Projectile : VirtualEntityComponent
    {
        //=============================================================================
        // State
        //=============================================================================

        /// <summary>
        /// Damage dealt on impact.
        /// </summary>
        public int damage = 10;

        /// <summary>
        /// Player ID who fired this projectile.
        /// </summary>
        public int shooterId = -1;

        /// <summary>
        /// Whether this projectile has hit something.
        /// </summary>
        public bool hasHit = false;

        /// <summary>
        /// Impact position (for effects).
        /// </summary>
        public Vector3 impactPoint;

        /// <summary>
        /// Impact normal (for effects).
        /// </summary>
        public Vector3 impactNormal;

        //=============================================================================
        // Component Type
        //=============================================================================

        public override int componentTypeId => ComponentTypes.Projectile;
        public override int serializedSize => 6; // damage(2) + shooterId(1) + hasHit(1) + reserved(2)

        //=============================================================================
        // Lifecycle
        //=============================================================================

        public override void OnSpawn()
        {
            hasHit = false;
            shooterId = Networking.LocalPlayer != null ? Networking.LocalPlayer.playerId : -1;
        }

        public override void OnCollision(VirtualEntity other, Vector3 point, Vector3 normal)
        {
            if (hasHit) return;

            hasHit = true;
            impactPoint = point;
            impactNormal = normal;

            // Apply damage if target has Damageable component
            var damageable = other.GetComponent<Damageable>();
            if (damageable != null)
            {
                damageable.TakeDamage(damage, shooterId);
            }

            // Destroy after impact
            entity.Destroy();
        }

        //=============================================================================
        // Serialization
        //=============================================================================

        public override void OnSerialize(byte[] data, ref int offset)
        {
            WriteInt16(data, ref offset, (short)damage);
            WriteByte(data, ref offset, (byte)shooterId);
            WriteByte(data, ref offset, hasHit ? (byte)1 : (byte)0);
            WriteInt16(data, ref offset, 0); // reserved
        }

        public override void OnDeserialize(byte[] data, ref int offset)
        {
            damage = ReadInt16(data, ref offset);
            shooterId = ReadByte(data, ref offset);
            hasHit = ReadByte(data, ref offset) != 0;
            offset += 2; // reserved
        }
    }
}
```

### Damageable

```csharp
namespace UdonSharp.CE.VirtualEntities.Components
{
    /// <summary>
    /// Component for entities that can take damage.
    /// </summary>
    [PublicAPI]
    public class Damageable : VirtualEntityComponent
    {
        //=============================================================================
        // State
        //=============================================================================

        /// <summary>
        /// Maximum health.
        /// </summary>
        public int maxHealth = 100;

        /// <summary>
        /// Current health.
        /// </summary>
        public int health = 100;

        /// <summary>
        /// Whether this entity is dead.
        /// </summary>
        public bool isDead => health <= 0;

        /// <summary>
        /// Player ID of last attacker.
        /// </summary>
        public int lastAttackerId = -1;

        /// <summary>
        /// Time of last damage taken.
        /// </summary>
        public float lastDamageTime;

        //=============================================================================
        // Events (CECallback)
        //=============================================================================

        /// <summary>
        /// Callback when damage is taken.
        /// </summary>
        public CECallback onDamage;

        /// <summary>
        /// Callback when entity dies.
        /// </summary>
        public CECallback onDeath;

        //=============================================================================
        // Component Type
        //=============================================================================

        public override int componentTypeId => ComponentTypes.Damageable;
        public override int serializedSize => 5; // maxHealth(2) + health(2) + lastAttackerId(1)

        //=============================================================================
        // Methods
        //=============================================================================

        /// <summary>
        /// Applies damage to this entity.
        /// </summary>
        public void TakeDamage(int amount, int attackerId = -1)
        {
            if (isDead) return;

            health -= amount;
            lastAttackerId = attackerId;
            lastDamageTime = Time.time;

            if (health < 0) health = 0;

            onDamage.Invoke();

            if (isDead)
            {
                onDeath.Invoke();

                // Optional: destroy entity on death
                // entity.Destroy();
            }
        }

        /// <summary>
        /// Heals the entity.
        /// </summary>
        public void Heal(int amount)
        {
            health = Mathf.Min(health + amount, maxHealth);
        }

        /// <summary>
        /// Resets health to maximum.
        /// </summary>
        public void ResetHealth()
        {
            health = maxHealth;
        }

        //=============================================================================
        // Serialization
        //=============================================================================

        public override void OnSerialize(byte[] data, ref int offset)
        {
            WriteInt16(data, ref offset, (short)maxHealth);
            WriteInt16(data, ref offset, (short)health);
            WriteByte(data, ref offset, (byte)lastAttackerId);
        }

        public override void OnDeserialize(byte[] data, ref int offset)
        {
            maxHealth = ReadInt16(data, ref offset);
            health = ReadInt16(data, ref offset);
            lastAttackerId = ReadByte(data, ref offset);
        }
    }
}
```

### HomingMissile

```csharp
namespace UdonSharp.CE.VirtualEntities.Components
{
    /// <summary>
    /// Homing missile that tracks a target entity.
    /// </summary>
    [PublicAPI]
    public class HomingMissile : VirtualEntityComponent
    {
        //=============================================================================
        // Configuration
        //=============================================================================

        /// <summary>
        /// Turn rate in degrees per second.
        /// </summary>
        public float turnRate = 180f;

        /// <summary>
        /// Forward speed.
        /// </summary>
        public float speed = 30f;

        /// <summary>
        /// Acceleration.
        /// </summary>
        public float acceleration = 10f;

        /// <summary>
        /// Maximum speed.
        /// </summary>
        public float maxSpeed = 50f;

        /// <summary>
        /// Time before homing activates.
        /// </summary>
        public float armingTime = 0.5f;

        //=============================================================================
        // State
        //=============================================================================

        /// <summary>
        /// Target entity to home towards.
        /// </summary>
        public VirtualEntity target;

        /// <summary>
        /// Target entity ID (for network sync).
        /// </summary>
        public int targetId = -1;

        /// <summary>
        /// Whether homing is active.
        /// </summary>
        public bool isArmed => Time.time - entity.spawnTime >= armingTime;

        //=============================================================================
        // Component Type
        //=============================================================================

        public override int componentTypeId => ComponentTypes.HomingMissile;
        public override int serializedSize => 8; // targetId(2) + speed(2) + turnRate(2) + maxSpeed(2)

        //=============================================================================
        // Update
        //=============================================================================

        public override void OnFixedUpdate()
        {
            if (!isArmed) return;

            // Resolve target from ID if needed
            if (target == null && targetId >= 0)
            {
                target = world.GetEntity(targetId);
            }

            if (target == null || !target.isValid)
            {
                // No target - fly straight
                velocity = entity.forward * speed;
                return;
            }

            // Calculate direction to target
            Vector3 toTarget = target.position - position;
            float distance = toTarget.magnitude;

            if (distance < 0.1f)
            {
                // Hit target
                return;
            }

            Vector3 targetDir = toTarget / distance;

            // Rotate towards target
            Quaternion targetRot = Quaternion.LookRotation(targetDir);
            rotation = Quaternion.RotateTowards(rotation, targetRot, turnRate * Time.fixedDeltaTime);

            // Accelerate
            speed = Mathf.MoveTowards(speed, maxSpeed, acceleration * Time.fixedDeltaTime);

            // Apply velocity
            velocity = entity.forward * speed;
        }

        //=============================================================================
        // Serialization
        //=============================================================================

        public override void OnSerialize(byte[] data, ref int offset)
        {
            WriteInt16(data, ref offset, (short)targetId);
            WriteInt16(data, ref offset, (short)(speed * 10f));
            WriteInt16(data, ref offset, (short)turnRate);
            WriteInt16(data, ref offset, (short)(maxSpeed * 10f));
        }

        public override void OnDeserialize(byte[] data, ref int offset)
        {
            targetId = ReadInt16(data, ref offset);
            speed = ReadInt16(data, ref offset) / 10f;
            turnRate = ReadInt16(data, ref offset);
            maxSpeed = ReadInt16(data, ref offset) / 10f;
        }
    }
}
```

### Collectible

```csharp
namespace UdonSharp.CE.VirtualEntities.Components
{
    /// <summary>
    /// Collectible item component.
    /// </summary>
    [PublicAPI]
    public class Collectible : VirtualEntityComponent
    {
        //=============================================================================
        // Configuration
        //=============================================================================

        /// <summary>
        /// Value when collected (points, coins, etc).
        /// </summary>
        public int value = 1;

        /// <summary>
        /// Item type for filtering.
        /// </summary>
        public int itemType = 0;

        /// <summary>
        /// Whether this can only be collected once.
        /// </summary>
        public bool singleUse = true;

        /// <summary>
        /// Respawn time if not single use.
        /// </summary>
        public float respawnTime = 10f;

        /// <summary>
        /// Collection radius.
        /// </summary>
        public float collectRadius = 1f;

        //=============================================================================
        // State
        //=============================================================================

        /// <summary>
        /// Whether currently available for collection.
        /// </summary>
        public bool isAvailable = true;

        /// <summary>
        /// Player ID who collected this.
        /// </summary>
        public int collectorId = -1;

        /// <summary>
        /// Time when collected (for respawn).
        /// </summary>
        public float collectedTime;

        //=============================================================================
        // Events
        //=============================================================================

        /// <summary>
        /// Callback when collected.
        /// </summary>
        public CECallback onCollected;

        /// <summary>
        /// Callback when respawned.
        /// </summary>
        public CECallback onRespawn;

        //=============================================================================
        // Component Type
        //=============================================================================

        public override int componentTypeId => ComponentTypes.Collectible;
        public override int serializedSize => 4; // value(2) + itemType(1) + flags(1)

        //=============================================================================
        // Methods
        //=============================================================================

        /// <summary>
        /// Attempts to collect this item.
        /// </summary>
        /// <returns>Value if collected, 0 otherwise.</returns>
        public int TryCollect(int playerId)
        {
            if (!isAvailable) return 0;

            isAvailable = false;
            collectorId = playerId;
            collectedTime = Time.time;

            onCollected.Invoke();

            if (singleUse)
            {
                entity.Destroy();
            }
            else
            {
                // Hide visual
                entity.activeSelf = false;
            }

            return value;
        }

        public override void OnUpdate()
        {
            if (!singleUse && !isAvailable)
            {
                if (Time.time - collectedTime >= respawnTime)
                {
                    isAvailable = true;
                    entity.activeSelf = true;
                    onRespawn.Invoke();
                }
            }
        }

        //=============================================================================
        // Serialization
        //=============================================================================

        public override void OnSerialize(byte[] data, ref int offset)
        {
            WriteInt16(data, ref offset, (short)value);
            WriteByte(data, ref offset, (byte)itemType);
            byte flags = 0;
            if (isAvailable) flags |= 1;
            if (singleUse) flags |= 2;
            WriteByte(data, ref offset, flags);
        }

        public override void OnDeserialize(byte[] data, ref int offset)
        {
            value = ReadInt16(data, ref offset);
            itemType = ReadByte(data, ref offset);
            byte flags = ReadByte(data, ref offset);
            isAvailable = (flags & 1) != 0;
            singleUse = (flags & 2) != 0;
        }
    }
}
```

---

## Component Type Registry

```csharp
namespace UdonSharp.CE.VirtualEntities
{
    /// <summary>
    /// Well-known component type IDs.
    /// User-defined components should use IDs >= 100.
    /// </summary>
    [PublicAPI]
    public static class ComponentTypes
    {
        // Core components (0-19)
        public const int None = 0;
        public const int Transform = 1;      // Reserved (built-in)
        public const int Rigidbody = 2;      // Reserved (built-in)

        // Combat components (20-39)
        public const int Projectile = 20;
        public const int Damageable = 21;
        public const int HomingMissile = 22;
        public const int Explosive = 23;
        public const int Weapon = 24;

        // Item components (40-59)
        public const int Collectible = 40;
        public const int Pickup = 41;
        public const int Consumable = 42;
        public const int Equipment = 43;

        // AI components (60-79)
        public const int AIController = 60;
        public const int Pathfinder = 61;
        public const int StateMachine = 62;

        // Effect components (80-99)
        public const int TimedDestroy = 80;
        public const int FadeOut = 81;
        public const int Bounce = 82;
        public const int Orbit = 83;
        public const int Oscillate = 84;

        // User components start at 100
        public const int UserDefinedStart = 100;
    }
}
```

---

## Network Protocol

### State Packet Format

```
┌──────────────────────────────────────────────────────────────────┐
│                         HEADER (10 bytes)                         │
├──────────────────────────────────────────────────────────────────┤
│ Sequence     │ Entity Count │ Timestamp     │ Flags             │
│ (4 bytes)    │ (2 bytes)    │ (4 bytes)     │ (implicit)        │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│                    ENTITY DATA (per entity)                       │
├──────────────────────────────────────────────────────────────────┤
│ Entity ID   │ Prefab ID  │ Flags      │ Owner     │ Position    │
│ (2 bytes)   │ (1 byte)   │ (2 bytes)  │ (1 byte)  │ (6 bytes)   │
├──────────────────────────────────────────────────────────────────┤
│ Rotation    │ Velocity (optional)     │ Scale (optional)        │
│ (6 bytes)   │ (5 bytes)               │ (6 bytes)               │
├──────────────────────────────────────────────────────────────────┤
│ Lifetime (optional)   │ Component Mask │ Component Data...      │
│ (2 bytes)             │ (2 bytes)      │ (variable)             │
└──────────────────────────────────────────────────────────────────┘

Optional fields included based on Flags:
- Bit 0: Include velocity
- Bit 1: Include angular velocity
- Bit 2: Include scale
- Bit 3: Include lifetime
- Bit 4: Include component data

Component Data:
- For each bit set in Component Mask, read that component's serializedSize bytes
```

### Spawn Request Format

```
┌──────────────────────────────────────────────────────────────────┐
│                    SPAWN REQUEST (24 bytes)                       │
├──────────────────────────────────────────────────────────────────┤
│ Request ID  │ Prefab ID  │ Requester  │ Position                 │
│ (2 bytes)   │ (1 byte)   │ (1 byte)   │ (12 bytes, full float)  │
├──────────────────────────────────────────────────────────────────┤
│ Rotation (8 bytes, compressed)                                   │
└──────────────────────────────────────────────────────────────────┘
```

### Despawn Request Format

```
┌──────────────────────────────────────────────────────────────────┐
│                    DESPAWN REQUEST (4 bytes)                      │
├──────────────────────────────────────────────────────────────────┤
│ Entity ID   │ Requester  │ Reserved                              │
│ (2 bytes)   │ (1 byte)   │ (1 byte)                              │
└──────────────────────────────────────────────────────────────────┘
```

---

## Integration with CE Systems

### CE.NetPhysics Integration

```csharp
// VirtualEntityWorld can optionally delegate physics to NetPhysicsWorld
public class VirtualEntityWorld
{
    [Header("CE.NetPhysics Integration")]
    [Tooltip("Optional NetPhysicsWorld for advanced physics simulation.")]
    public NetPhysicsWorld netPhysicsWorld;

    private void SimulatePhysics(float dt)
    {
        if (netPhysicsWorld != null)
        {
            // Use NetPhysics rollback-capable simulation
            // Virtual entities are registered as NetPhysicsEntity instances
        }
        else
        {
            // Simple built-in physics
            SimulateBuiltInPhysics(dt);
        }
    }
}
```

### CE.Persistence Integration

```csharp
// Save/restore virtual entity world state
public void SaveWorld()
{
    byte[] state = _compressor.Compress(this, _activeList, _activeCount);
    CEPersistence.SaveBytes("virtual_entities", state);
}

public void RestoreWorld()
{
    if (CEPersistence.TryLoadBytes("virtual_entities", out byte[] state))
    {
        DespawnAll();
        _compressor.Decompress(state, this);
        UpdateVisuals();
    }
}
```

### CE.Perf Integration

```csharp
// VirtualEntityWorld uses CEPool for visual management
private void InitializePools()
{
    _visualPools = new CEPool<GameObject>[_prefabs.Length];

    for (int i = 0; i < _prefabs.Length; i++)
    {
        var prefab = _prefabs[i];
        if (prefab == null || prefab.visualPrefab == null) continue;

        int poolSize = (int)(_maxEntities * _poolSizeMultiplier);
        _visualPools[i] = new CEPool<GameObject>(poolSize);
        _visualPools[i].Initialize(
            index => Instantiate(prefab.visualPrefab),
            visual => visual.SetActive(true),
            visual => visual.SetActive(false)
        );
    }
}
```

---

## Performance Considerations

### Memory Budget

```
Per Entity (arrays):
- Core state: ~100 bytes
  - Position/Rotation/Velocity: 52 bytes
  - Scale: 12 bytes
  - Flags/IDs: 24 bytes
  - Misc: 12 bytes

- Component storage: ~64 bytes per entity slot
- Visual reference: 8 bytes

128 entities = ~22 KB base storage
```

### Network Budget

```
VRChat sync limits: ~200 bytes per sync call

With 21 bytes per entity base:
- 8-9 entities per packet (without component data)
- At 20Hz sync rate: 160-180 entities/second throughput

Chunked sync for full world state:
- 128 entities = ~2.7 KB
- Split into 14 packets at 200 bytes each
- Full sync in ~0.7 seconds at 20Hz
```

### CPU Budget

```
Per frame:
- Physics simulation: O(n) where n = physics-enabled entities
- Visual updates: O(n) where n = active entities
- Interpolation: O(n) with SIMD-friendly array iteration

Per sync:
- Compression: O(k) where k = entities in packet
- Decompression: O(k)
```

---

## File Structure

```
Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/VirtualEntities/
├── Core/
│   ├── VirtualEntity.cs
│   ├── VirtualEntityWorld.cs
│   ├── VirtualEntityPrefab.cs
│   ├── VirtualEntityComponent.cs
│   ├── EntityFlags.cs
│   └── ComponentTypes.cs
├── Compression/
│   └── VirtualEntityCompressor.cs
├── Components/
│   ├── Projectile.cs
│   ├── Damageable.cs
│   ├── HomingMissile.cs
│   ├── Collectible.cs
│   ├── AIController.cs
│   ├── TimedDestroy.cs
│   └── Oscillate.cs
└── Editor/
    ├── VirtualEntityPrefabEditor.cs
    ├── VirtualEntityWorldEditor.cs
    └── VirtualEntityDebugWindow.cs
```

---

## Usage Examples

### Basic Projectile System

```csharp
public class WeaponController : UdonSharpBehaviour
{
    public VirtualEntityPrefab bulletPrefab;
    public VirtualEntityPrefab rocketPrefab;
    public Transform muzzle;
    public float fireRate = 10f;

    private float _lastFireTime;

    void Update()
    {
        if (Input.GetButton("Fire1") && Time.time - _lastFireTime >= 1f / fireRate)
        {
            FireBullet();
            _lastFireTime = Time.time;
        }

        if (Input.GetButtonDown("Fire2"))
        {
            FireRocket();
        }
    }

    void FireBullet()
    {
        var bullet = VirtualEntity.Instantiate(bulletPrefab, muzzle.position, muzzle.rotation);
        bullet.velocity = muzzle.forward * 100f;
        bullet.Destroy(3f);
    }

    void FireRocket()
    {
        var rocket = VirtualEntity.Instantiate(rocketPrefab, muzzle.position, muzzle.rotation);
        rocket.velocity = muzzle.forward * 20f;

        var homing = rocket.GetComponent<HomingMissile>();
        homing.target = FindNearestEnemy();
    }

    VirtualEntity FindNearestEnemy()
    {
        return VirtualEntity.FindNearest(muzzle.position + muzzle.forward * 10f, 50f);
    }
}
```

### Enemy Spawner

```csharp
public class EnemySpawner : UdonSharpBehaviour
{
    public VirtualEntityPrefab[] enemyPrefabs;
    public Transform[] spawnPoints;
    public float spawnInterval = 5f;
    public int maxEnemies = 20;

    private VirtualEntity[] _activeEnemies;
    private int _enemyCount;
    private float _lastSpawnTime;

    void Start()
    {
        _activeEnemies = new VirtualEntity[maxEnemies];
    }

    void Update()
    {
        // Clean up destroyed enemies
        for (int i = _enemyCount - 1; i >= 0; i--)
        {
            if (_activeEnemies[i] == null || !_activeEnemies[i].isValid)
            {
                _activeEnemies[i] = _activeEnemies[_enemyCount - 1];
                _enemyCount--;
            }
        }

        // Spawn new enemies
        if (_enemyCount < maxEnemies && Time.time - _lastSpawnTime >= spawnInterval)
        {
            SpawnEnemy();
            _lastSpawnTime = Time.time;
        }
    }

    void SpawnEnemy()
    {
        var prefab = enemyPrefabs[Random.Range(0, enemyPrefabs.Length)];
        var spawnPoint = spawnPoints[Random.Range(0, spawnPoints.Length)];

        var enemy = VirtualEntity.Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);

        var damageable = enemy.GetComponent<Damageable>();
        damageable.onDeath = new CECallback(this, nameof(OnEnemyDeath));

        _activeEnemies[_enemyCount] = enemy;
        _enemyCount++;
    }

    public void OnEnemyDeath()
    {
        // Award points, spawn loot, etc.
    }
}
```

### Coin Collection Game

```csharp
public class CoinGame : UdonSharpBehaviour
{
    public VirtualEntityPrefab coinPrefab;
    public int coinsToSpawn = 50;
    public Vector3 spawnAreaMin;
    public Vector3 spawnAreaMax;

    private int _score;

    void Start()
    {
        SpawnCoins();
    }

    void SpawnCoins()
    {
        for (int i = 0; i < coinsToSpawn; i++)
        {
            Vector3 pos = new Vector3(
                Random.Range(spawnAreaMin.x, spawnAreaMax.x),
                Random.Range(spawnAreaMin.y, spawnAreaMax.y),
                Random.Range(spawnAreaMin.z, spawnAreaMax.z)
            );

            var coin = VirtualEntity.Instantiate(coinPrefab, pos, Random.rotation);

            var collectible = coin.GetComponent<Collectible>();
            collectible.value = Random.Range(1, 10);
            collectible.onCollected = new CECallback(this, nameof(OnCoinCollected));
        }
    }

    public void OnCoinCollected()
    {
        // Score is tracked via events
    }

    void Update()
    {
        // Check for coin collection based on player proximity
        var player = Networking.LocalPlayer;
        if (player == null) return;

        Vector3 playerPos = player.GetPosition();

        VirtualEntity[] nearby = new VirtualEntity[10];
        int count = VirtualEntity.FindInRadius(playerPos, 2f, nearby);

        for (int i = 0; i < count; i++)
        {
            var collectible = nearby[i].GetComponent<Collectible>();
            if (collectible != null && collectible.isAvailable)
            {
                int value = collectible.TryCollect(player.playerId);
                _score += value;
            }
        }
    }
}
```

---

## Future Considerations

### Phase 2 Enhancements

1. **Hierarchical Transforms** - Full parent-child transform chains
2. **LOD System** - Reduce sync frequency for distant entities
3. **Interest Management** - Only sync entities relevant to each player
4. **Delta Compression** - Only sync changed properties
5. **Prediction** - Client-side extrapolation for smoother visuals

### Phase 3 Enhancements

1. **Pathfinding Integration** - NavMesh-like navigation for AI
2. **Spatial Partitioning** - Octree for efficient queries
3. **Replay System** - Record and playback entity state
4. **Editor Tools** - Visual debugging, profiling

---

## Revision History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2024-12 | Initial design specification |
