# CE Runtime Performance — Technical Proposal

_Automatic Performance Improvements Without Developer Effort_

**Version:** 2.1  
**Date:** December 2025  
**Status:** Phase 1 Complete ✅ — All library optimizations shipped; compiler pipeline (Phases 2-4) pending

---

## Executive Summary

UdonSharpCE delivers runtime performance improvements through two complementary approaches:

1. **Library Optimizations**: Faster built-in collections and ECS systems — benefits all users automatically
2. **Compiler Optimizations**: Intelligent code generation — most applied automatically, power features opt-in

The guiding principle: **maximum impact with minimum developer effort**. Users should get faster worlds simply by using CE, without learning new APIs or adding attributes.

### Design Philosophy

```
┌─────────────────────────────────────────────────────────────────┐
│                    CE Performance Tiers                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  TIER 1: Invisible (100% of users benefit)                      │
│  ─────────────────────────────────────────────────────────────  │
│  • Library fixes: Faster Dictionary, List, HashSet, ECS         │
│  • Compiler: Constant folding, dead code, string interning      │
│  • Zero configuration, zero awareness required                  │
│                                                                 │
│  TIER 2: Smart Defaults (Most users benefit)                    │
│  ─────────────────────────────────────────────────────────────  │
│  • Auto sync packing for adjacent small variables               │
│  • Auto delta sync for Vector3/Quaternion                       │
│  • Disable with attribute if causing issues                     │
│                                                                 │
│  TIER 3: Opt-in Power Features (Advanced users)                 │
│  ─────────────────────────────────────────────────────────────  │
│  • [CEPooled] for object pooling                                │
│  • [CECompressSync] for bandwidth-critical sync                 │
│  • Requires explicit attribute and understanding                │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Expected Impact

| Category              | Improvement      | Developer Effort               |
| --------------------- | ---------------- | ------------------------------ |
| Collection operations | 20-50% faster    | None                           |
| ECS query iteration   | 30-60% faster    | None                           |
| Hash-based lookups    | 15-25% faster    | None                           |
| Sync bandwidth        | 30-70% reduction | None (auto) to minimal (hints) |
| Instruction count     | 10-25% reduction | None                           |
| Memory allocations    | 40-60% reduction | None to minimal                |

---

## Part 1: Library Optimizations

These optimizations improve CE's built-in libraries. **All users benefit automatically** — no code changes, no attributes, no configuration.

### 1.1 Hash Index Computation Fix

**Problem**: All hash-based collections use `Mathf.Abs(hashCode % capacity)` to compute bucket indices. `Mathf.Abs()` is an extern method call in Udon — significantly more expensive than bitwise operations.

**Affected Files**:

- `Collections/Dictionary.cs` (7 occurrences)
- `Collections/HashSet.cs` (4 occurrences)
- `CE/Data/CEDictionary.cs` (2 occurrences)

**Solution**:

```csharp
// Before (slow - extern call)
int index = Mathf.Abs(hashCode % capacity);

// After (fast - bitwise operation)
int index = (hashCode & 0x7FFFFFFF) % capacity;
```

**Why This Works**: `& 0x7FFFFFFF` clears the sign bit, making the value non-negative. This is a single CPU instruction versus a method call, stack manipulation, and return.

**Impact**: 15-25% faster for all Dictionary/HashSet operations.

---

### 1.2 CEWorld Pending Destruction Optimization

**Problem**: `ProcessPendingDestructions()` scans ALL entities every tick, even when no entities are pending destruction.

```csharp
// Current: O(n) every tick regardless of pending count
private void ProcessPendingDestructions()
{
    for (int i = 0; i < _entityCount; i++)  // Scans ALL entities
    {
        if (_entityStates[i] == EntityState.PendingDestroy)
        {
            // Process destruction
        }
    }
}
```

**Solution**: Maintain a separate pending destruction list:

```csharp
// New fields
private int[] _pendingDestroyList;
private int _pendingDestroyCount;

// O(1) check, O(k) processing where k = pending count
private void ProcessPendingDestructions()
{
    if (_pendingDestroyCount == 0) return;  // Early exit - common case

    for (int i = 0; i < _pendingDestroyCount; i++)
    {
        int entityId = _pendingDestroyList[i];
        ProcessDestruction(entityId);
    }

    _pendingDestroyCount = 0;
}

public void DestroyEntityDeferred(int entityId)
{
    _entityStates[entityId] = EntityState.PendingDestroy;

    // Add to pending list
    if (_pendingDestroyCount >= _pendingDestroyList.Length)
        ExpandPendingList();

    _pendingDestroyList[_pendingDestroyCount++] = entityId;
}
```

**Impact**: Near-zero cost when no entities pending (common case). Worlds with 1000+ entities see significant frame time reduction.

---

### 1.3 CEQuery Hot Path Optimization

**Problem**: `ForEach`, `Execute`, and `Count` all call `Matches(i)` per entity, which internally makes 3 method calls:

```csharp
// Current: 3 method calls per entity
private bool Matches(int entityId)
{
    if (!_world.IsValidEntity(entityId)) return false;  // Call 1
    int mask = _world.GetComponentMask(entityId);        // Call 2
    return (mask & _requiredMask) == _requiredMask &&    // Call 3 (implicit)
           (mask & _excludedMask) == 0;
}

public void ForEach(Action<int> action)
{
    for (int i = 0; i < _world.EntityCount; i++)
    {
        if (Matches(i))  // 3 calls per entity!
            action(i);
    }
}
```

**Solution**: Direct array access for hot loops:

```csharp
// CEWorld exposes internal arrays for trusted access
internal EntityState[] GetEntityStates() => _entityStates;
internal int[] GetComponentMasks() => _componentMasks;

// CEQuery optimized iteration
public void ForEach(Action<int> action)
{
    var states = _world.GetEntityStates();
    var masks = _world.GetComponentMasks();
    int count = _world.EntityCount;
    int required = _requiredMask;
    int excluded = _excludedMask;

    for (int i = 0; i < count; i++)
    {
        // Inlined checks - no method calls
        if (states[i] != EntityState.Active) continue;
        int mask = masks[i];
        if ((mask & required) != required) continue;
        if ((mask & excluded) != 0) continue;

        action(i);
    }
}
```

**Impact**: 30-60% faster query iteration. Significant for worlds with many entities and frequent queries.

---

### 1.4 CEPool O(1) Release

**Problem**: `Release(T obj)` performs O(n) linear search to find the object's index:

```csharp
// Current: O(n) search
public void Release(T obj)
{
    for (int i = 0; i < _pool.Length; i++)  // Linear search!
    {
        if (ReferenceEquals(_pool[i], obj))
        {
            ReleaseByIndex(i);
            return;
        }
    }
}
```

**Solution**: Return a handle from `Acquire()` that includes the index:

```csharp
/// <summary>
/// Handle for a pooled object. Use this for O(1) release.
/// </summary>
public struct PoolHandle<T>
{
    public readonly int Index;
    public readonly T Object;

    internal PoolHandle(int index, T obj)
    {
        Index = index;
        Object = obj;
    }

    public bool IsValid => Index >= 0;
    public static PoolHandle<T> Invalid => new PoolHandle<T>(-1, default);
}

// New API
public PoolHandle<T> AcquireHandle()
{
    if (_availableCount == 0)
    {
        if (!TryExpand()) return PoolHandle<T>.Invalid;
    }

    int index = _availableIndices[--_availableCount];
    _inUse[index] = true;
    return new PoolHandle<T>(index, _pool[index]);
}

public void Release(PoolHandle<T> handle)
{
    if (!handle.IsValid) return;
    ReleaseByIndex(handle.Index);  // O(1)!
}

// Keep old API for compatibility, but document performance
[Obsolete("Use AcquireHandle() and Release(handle) for O(1) performance")]
public void Release(T obj) { /* O(n) search */ }
```

**Usage**:

```csharp
// Before (O(n) release)
Bullet bullet = bulletPool.Acquire();
// ... use bullet ...
bulletPool.Release(bullet);  // O(n) search

// After (O(1) release)
var handle = bulletPool.AcquireHandle();
Bullet bullet = handle.Object;
// ... use bullet ...
bulletPool.Release(handle);  // O(1) direct index
```

**Impact**: O(n) → O(1) for release operations. Critical for pools with many objects (projectiles, particles, etc.).

---

### 1.5 Collection Unchecked Access

**Problem**: Every indexed access performs bounds checking. For hot loops where bounds are guaranteed externally, this is wasteful.

```csharp
// Current: Bounds check every access
public T this[int index]
{
    get
    {
        if (index < 0 || index >= _size)  // Check every time
            throw new IndexOutOfRangeException();
        return _items[index];
    }
}
```

**Solution**: Add unchecked access methods for performance-critical code:

```csharp
/// <summary>
/// Get item without bounds checking. Only use when index is guaranteed valid.
/// </summary>
public T GetUnchecked(int index) => _items[index];

/// <summary>
/// Set item without bounds checking. Only use when index is guaranteed valid.
/// </summary>
public void SetUnchecked(int index, T value) => _items[index] = value;

/// <summary>
/// Direct array access for advanced usage. Length may exceed Count.
/// </summary>
public T[] GetBackingArray() => _items;
```

**Affected Classes**:

- `List<T>` / `CEList<T>`
- `Dictionary<K,V>` / `CEDictionary<K,V>` (for values array)
- `Queue<T>` / `Stack<T>`

**Usage**:

```csharp
// Hot loop with guaranteed bounds
var list = GetEnemies();
int count = list.Count;
for (int i = 0; i < count; i++)
{
    // Safe: i is always in bounds
    Enemy enemy = list.GetUnchecked(i);
    enemy.Update();
}
```

**Impact**: 10-20% faster for tight loops over collections.

---

### 1.6 Iterator Allocation Elimination

**Problem**: Every `foreach` loop allocates a new iterator object:

```csharp
// This allocates!
foreach (var item in myList)
{
    Process(item);
}

// Equivalent to:
var iterator = myList.GetEnumerator();  // ALLOCATION
while (iterator.MoveNext())
{
    var item = iterator.Current;
    Process(item);
}
```

**Solution**: Add `ForEach` methods that avoid allocation:

```csharp
// List<T> additions
public void ForEach(Action<T> action)
{
    for (int i = 0; i < _size; i++)
    {
        action(_items[i]);
    }
}

public void ForEachWithIndex(Action<T, int> action)
{
    for (int i = 0; i < _size; i++)
    {
        action(_items[i], i);
    }
}

// For when you need to break early
public void ForEachUntil(Func<T, bool> predicate)
{
    for (int i = 0; i < _size; i++)
    {
        if (!predicate(_items[i])) return;
    }
}
```

**Also**: Cache a single iterator instance per collection (safe because Udon is single-threaded):

```csharp
private ListIterator _cachedIterator;

public ListIterator GetEnumerator()
{
    if (_cachedIterator == null)
        _cachedIterator = new ListIterator(this);
    else
        _cachedIterator.Reset();

    return _cachedIterator;
}
```

**Impact**: Eliminates 1 allocation per `foreach` loop. Significant for frequently iterated collections.

---

### 1.7 CEDictionary Tombstone Fix (Bug + Optimization)

**Problem**: `CEDictionary` uses open addressing but doesn't handle deletion correctly. The `FindKeyIndex` method breaks on the first empty slot, but after deletion this causes lookups to fail:

```csharp
// Current (buggy): Breaks on empty, but deletion leaves "holes"
private int FindKeyIndex(TKey key)
{
    int index = (key.GetHashCode() & 0x7FFFFFFF) % _capacity;

    while (_occupied[index])  // Stops at first empty slot!
    {
        if (_keys[index].Equals(key))
            return index;
        index = (index + 1) % _capacity;
    }

    return -1;  // May incorrectly return -1 if hole exists before target
}

// Current (expensive): Rehashes entire table on every removal
public bool Remove(TKey key)
{
    int index = FindKeyIndex(key);
    if (index < 0) return false;

    _occupied[index] = false;
    _count--;

    RehashAfterRemoval();  // O(n) every removal!
    return true;
}
```

**Solution**: Implement tombstone deletion:

```csharp
// Slot states
private const byte EMPTY = 0;
private const byte OCCUPIED = 1;
private const byte TOMBSTONE = 2;

private byte[] _slotState;  // Replaces bool[] _occupied

private int FindKeyIndex(TKey key)
{
    int index = (key.GetHashCode() & 0x7FFFFFFF) % _capacity;
    int startIndex = index;

    while (_slotState[index] != EMPTY)  // Continue past tombstones
    {
        if (_slotState[index] == OCCUPIED && _keys[index].Equals(key))
            return index;

        index = (index + 1) % _capacity;
        if (index == startIndex) break;  // Full loop
    }

    return -1;
}

public bool Remove(TKey key)
{
    int index = FindKeyIndex(key);
    if (index < 0) return false;

    _slotState[index] = TOMBSTONE;  // Mark as tombstone, don't rehash
    _count--;
    _tombstoneCount++;

    // Only rehash when tombstones exceed threshold
    if (_tombstoneCount > _capacity / 4)
        Rehash();

    return true;
}

private int FindInsertIndex(TKey key)
{
    int index = (key.GetHashCode() & 0x7FFFFFFF) % _capacity;
    int firstTombstone = -1;

    while (_slotState[index] == OCCUPIED)
    {
        if (_keys[index].Equals(key))
            return index;  // Key exists

        index = (index + 1) % _capacity;
    }

    // Can insert at tombstone or empty slot
    if (_slotState[index] == TOMBSTONE && firstTombstone < 0)
        firstTombstone = index;

    return firstTombstone >= 0 ? firstTombstone : index;
}
```

**Impact**:

- **Correctness**: Fixes bug where lookups fail after deletions
- **Performance**: Removal becomes O(1) amortized instead of O(n)

---

## Part 2: Compiler Optimizations — Automatic

These optimizations are applied **automatically** during compilation. No attributes, no configuration, no developer awareness required.

### 2.1 Compilation Pipeline

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        CE Compilation Pipeline                          │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  C# Source                                                              │
│      │                                                                  │
│      ▼                                                                  │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  STAGE 1: CE Analyzer                                           │   │
│  │  • Detect optimization opportunities                            │   │
│  │  • Identify constant expressions                                │   │
│  │  • Find dead code paths                                         │   │
│  │  • Locate duplicate strings                                     │   │
│  │  • Detect adjacent sync variables                               │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│      │                                                                  │
│      ▼                                                                  │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  STAGE 2: CE Transformer                                        │   │
│  │  • Apply automatic optimizations                                │   │
│  │  • Generate sync packing code                                   │   │
│  │  • Inline small loops                                           │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│      │                                                                  │
│      ▼                                                                  │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  STAGE 3: UdonSharp Compiler (unmodified)                       │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│      │                                                                  │
│      ▼                                                                  │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  STAGE 4: CE Assembly Optimizer                                 │   │
│  │  • Constant folding                                             │   │
│  │  • Dead code elimination                                        │   │
│  │  • String interning                                             │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│      │                                                                  │
│      ▼                                                                  │
│  Optimized Program Asset                                                │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Constant Folding

**What It Does**: Evaluates constant expressions at compile time.

```csharp
// Before (runtime computation)
float circumference = 2.0f * 3.14159f * radius;
float gravity = 9.81f / 2.0f;
int flags = 1 | 2 | 4;

// After (compile-time evaluation)
float circumference = 6.28318f * radius;
float gravity = 4.905f;
int flags = 7;
```

**Patterns Detected**:

- Arithmetic on literals: `2 * 3` → `6`
- Const field references: `MY_CONST * 2` → evaluated
- Math functions with constant args: `Mathf.Sqrt(4)` → `2`
- Bitwise operations: `1 << 4` → `16`

**Automatically Applied**: Yes, always.

### 2.3 Dead Code Elimination

**What It Does**: Removes code that can never execute.

```csharp
// Before
void Update()
{
    DoImportantWork();

    if (false)
    {
        DoDebugStuff();  // Never executes
    }

    return;

    CleanupCode();  // Unreachable
}

// After
void Update()
{
    DoImportantWork();
}
```

**Patterns Detected**:

- `if (false)` / `if (true)` branches
- Code after unconditional `return`/`break`/`continue`
- Unused variable assignments (when variable never read)

**Automatically Applied**: Yes, always.

### 2.4 String Interning

**What It Does**: Deduplicates identical string literals across all scripts.

```csharp
// Before (3 separate string allocations)
// In PlayerController.cs:
Debug.Log("Game Started");
SendCustomEvent("Game Started");

// In GameManager.cs:
DisplayMessage("Game Started");

// After (1 string, 3 references)
// Generated:
private static readonly string _ce_str_42 = "Game Started";

// All references use _ce_str_42
```

**Automatically Applied**: Yes, always.

**Impact**: 80-95% reduction in duplicate string memory.

### 2.5 Small Loop Unrolling

**What It Does**: Replaces small fixed-iteration loops with straight-line code.

```csharp
// Before (loop overhead per iteration)
for (int i = 0; i < 4; i++)
{
    corners[i] = transform.TransformPoint(localCorners[i]);
}

// After (no loop overhead)
corners[0] = transform.TransformPoint(localCorners[0]);
corners[1] = transform.TransformPoint(localCorners[1]);
corners[2] = transform.TransformPoint(localCorners[2]);
corners[3] = transform.TransformPoint(localCorners[3]);
```

**Criteria for Automatic Unrolling**:

- Iteration count is constant
- Iteration count ≤ 4
- Loop body is simple (≤ 5 statements)
- No `break`/`continue`/`return` inside

**Automatically Applied**: Yes, when criteria met.

### 2.6 Tiny Method Inlining

**What It Does**: Replaces calls to very small methods with their body.

```csharp
// Before
float Square(float x) => x * x;
float Length(float x, float y) => Mathf.Sqrt(Square(x) + Square(y));

void Update()
{
    float dist = Length(dx, dy);
}

// After (inlined)
void Update()
{
    float dist = Mathf.Sqrt((dx * dx) + (dy * dy));
}
```

**Criteria for Automatic Inlining**:

- Method body is single expression or ≤ 2 statements
- Method is called ≥ 2 times
- No recursion
- Not virtual/override

**Automatically Applied**: Yes, when criteria met.

---

## Part 3: Compiler Optimizations — Smart Defaults

These optimizations are **enabled by default** but can be disabled if causing issues. Most users never need to think about them.

### 3.1 Auto Sync Packing

**What It Does**: Automatically packs adjacent small sync variables into larger types.

```csharp
// User writes (natural style)
[UdonSynced] private byte health;
[UdonSynced] private byte armor;
[UdonSynced] private byte stamina;
[UdonSynced] private byte mana;

// CE automatically generates
[UdonSynced] private uint _ce_packed_0;

private byte health
{
    get => (byte)(_ce_packed_0 & 0xFF);
    set => _ce_packed_0 = (_ce_packed_0 & 0xFFFFFF00) | value;
}
// ... etc
```

**Packing Rules**:

| Pattern              | Packing  | Sync Reduction |
| -------------------- | -------- | -------------- |
| 2-4 adjacent `byte`  | → `uint` | 50-75%         |
| 2 adjacent `ushort`  | → `uint` | 50%            |
| 2-8 adjacent `bool`  | → `byte` | 50-87.5%       |
| 9-32 adjacent `bool` | → `uint` | 87.5-96.9%     |

**Disable If Needed**:

```csharp
// Prevent specific variable from being packed
[CENoPackSync]
[UdonSynced] private byte health;

// Or disable for entire class
[CENoPackSync]
public class MyBehaviour : UdonSharpBehaviour { }
```

**When to Disable**: If you need individual `FieldChangeCallback` per variable, or if packing causes unexpected timing issues with manual sync.

### 3.2 Auto Delta Sync

**What It Does**: Automatically applies delta synchronization to Vector3 and Quaternion sync variables.

```csharp
// User writes
[UdonSynced] private Vector3 position;
[UdonSynced] private Quaternion rotation;

// CE automatically adds delta detection
private Vector3 _ce_position_last;
private float _ce_position_lastTime;

public override void OnPreSerialization()
{
    // Only sync if moved more than 1cm or 5 seconds elapsed
    if (Vector3.Distance(position, _ce_position_last) < 0.01f &&
        Time.time - _ce_position_lastTime < 5f)
    {
        // Revert to last synced value (VRChat detects no change, skips send)
        position = _ce_position_last;
    }
    else
    {
        _ce_position_last = position;
        _ce_position_lastTime = Time.time;
    }

    // Similar for rotation...
}
```

**Default Thresholds**:

| Type       | Movement Threshold   | Time Threshold |
| ---------- | -------------------- | -------------- |
| Vector3    | 0.01 units (1cm)     | 5 seconds      |
| Quaternion | 0.01 radians (~0.5°) | 5 seconds      |

**Disable If Needed**:

```csharp
// Disable delta sync for specific variable
[CEDeltaSync(false)]
[UdonSynced] private Vector3 position;

// Or customize thresholds
[CEDeltaSync(threshold: 0.001f, maxInterval: 1f)]
[UdonSynced] private Vector3 precisePosition;
```

**When to Disable**: If you need every position update to sync (e.g., recording system), or if you have custom sync logic.

---

## Part 4: Compiler Optimizations — Opt-in

These optimizations require explicit attributes because they change semantics or require developer understanding.

### 4.1 Object Pooling Generation

**Attribute**: `[CEPooled]`

**What It Does**: Generates a complete object pool system for a behaviour class.

```csharp
[CEPooled(initialSize: 20, maxSize: 50)]
public class Bullet : UdonSharpBehaviour
{
    public float speed;
    private Vector3 direction;

    // Called when acquired from pool
    public void CEOnAcquire()
    {
        gameObject.SetActive(true);
    }

    // Called when returned to pool
    public void CEOnRelease()
    {
        gameObject.SetActive(false);
    }

    public void Fire(Vector3 dir)
    {
        direction = dir.normalized;
    }
}

// Usage (generated Bullet_CEPool class):
public class Gun : UdonSharpBehaviour
{
    [SerializeField] private Bullet_CEPool bulletPool;

    public void Shoot()
    {
        var handle = bulletPool.AcquireHandle();
        if (handle.IsValid)
        {
            handle.Object.Fire(transform.forward);

            // Return after 5 seconds
            SendCustomEventDelayedSeconds(nameof(ReturnBullet), 5f);
        }
    }
}
```

**Parameters**:

| Parameter        | Default | Description                   |
| ---------------- | ------- | ----------------------------- |
| `initialSize`    | 10      | Starting pool size            |
| `maxSize`        | -1      | Maximum size (-1 = unlimited) |
| `allowExpansion` | true    | Can grow beyond initial size  |
| `expansionSize`  | 5       | Objects added when expanding  |

### 4.2 Sync Compression

**Attribute**: `[CECompressSync]`

**What It Does**: Applies lossy compression to floating-point sync variables to reduce bandwidth.

```csharp
// Half precision (16-bit float)
[CECompressSync(CECompressMode.Half)]
[UdonSynced] private float temperature;

// Range compression (0-100 mapped to byte)
[CECompressSync(CECompressMode.Range, min: 0, max: 100, bits: 8)]
[UdonSynced] private float percentage;  // ~0.4 precision

// Position with centimeter precision
[CECompressSync(CECompressMode.Centimeter)]
[UdonSynced] private Vector3 position;
```

**Compression Modes**:

| Mode         | Description          | Precision Loss    |
| ------------ | -------------------- | ----------------- |
| `Half`       | 16-bit float         | ~3 decimal digits |
| `Range`      | Map to integer range | Configurable      |
| `Centimeter` | Round to 0.01        | ±0.5cm            |
| `Millimeter` | Round to 0.001       | ±0.5mm            |
| `Degree`     | Round to 1°          | ±0.5°             |

**Bandwidth Reduction**:

| Type    | Uncompressed | Half    | Range (8-bit) |
| ------- | ------------ | ------- | ------------- |
| float   | 32 bits      | 16 bits | 8 bits        |
| Vector3 | 96 bits      | 48 bits | 24 bits       |

### 4.3 Event Batching

**Attribute**: `[CEBatchEvents]`

**What It Does**: Batches multiple network events from a method into fewer transmissions.

```csharp
[CEBatchEvents]
public void EndRound()
{
    // These batch into fewer network operations
    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(StopTimer));
    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ShowScoreboard));
    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySound));
    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ResetPlayers));
}
```

### 4.4 Debug-Only Code

**Attribute**: `[CEDebugOnly]`

**What It Does**: Completely removes marked methods and their call sites from release builds.

```csharp
[CEDebugOnly]
private void DrawDebugGizmos()
{
    // Complex visualization code
    // Completely removed in release builds
}

void Update()
{
    MovePlayer();
    DrawDebugGizmos();  // Call removed in release
}
```

### 4.5 Manual Optimization Hints

For cases where automatic detection isn't sufficient:

```csharp
// Force loop unrolling even if criteria not met
[CEUnroll(maxIterations: 8)]
void ProcessOctants()
{
    for (int i = 0; i < 8; i++) { /* ... */ }
}

// Force method inlining
[CEInline]
float CalculateWeight(float distance) => 1f / (distance * distance);

// Force compile-time evaluation
[CEConst]
private float TWO_PI = 2f * Mathf.PI;

// Prevent all CE optimizations on a member
[CENoOptimize]
public void CriticalMethod() { /* ... */ }
```

---

## Part 5: Disable Attributes Reference

When automatic optimizations cause issues, these attributes disable them:

| Attribute              | Effect                                       |
| ---------------------- | -------------------------------------------- |
| `[CENoOptimize]`       | Disable ALL CE optimizations on member/class |
| `[CENoPackSync]`       | Disable sync packing for variable/class      |
| `[CEDeltaSync(false)]` | Disable delta sync for variable              |
| `[CENoInline]`         | Prevent method from being inlined            |
| `[CENoUnroll]`         | Prevent loop from being unrolled             |

**Class-level example**:

```csharp
// Disable all automatic optimizations for this class
[CENoOptimize]
public class LegacyBehaviour : UdonSharpBehaviour
{
    // Compiles exactly as written, no CE transformations
}
```

---

## Part 6: Optimization Reports

CE generates reports showing what optimizations were applied:

### 6.1 Per-Script Report

```
════════════════════════════════════════════════════════════════════
CE Optimization Report: PlayerController.cs
════════════════════════════════════════════════════════════════════

AUTOMATIC OPTIMIZATIONS APPLIED
─────────────────────────────────────────────────────────────────────
✓ Constant Folding: 3 expressions
  • Line 42: 2.0f * Mathf.PI → 6.28318f
  • Line 67: SPEED * SCALE → 15.0f
  • Line 89: 1 << 4 → 16

✓ Dead Code Elimination: 8 statements
  • Lines 150-158: if (DEBUG) block removed

✓ String Interning: 4 strings deduplicated
  • "PlayerJoined" (3 occurrences → 1)

✓ Loop Unrolling: 1 loop
  • Line 120: for (i < 4) unrolled

✓ Method Inlining: 2 methods
  • Square() inlined at 3 sites
  • Clamp01() inlined at 2 sites

SMART DEFAULT OPTIMIZATIONS
─────────────────────────────────────────────────────────────────────
✓ Sync Packing: 4 bytes → 1 uint
  • health, armor, stamina, mana

✓ Delta Sync: 2 variables
  • position (threshold: 0.01)
  • rotation (threshold: 0.01)

SUMMARY
─────────────────────────────────────────────────────────────────────
Network bandwidth: -65% estimated
Instruction count: -18%
String memory: -75%

════════════════════════════════════════════════════════════════════
```

### 6.2 Viewing Reports

- **Automatic**: Report shown in Console after compilation (collapsible)
- **On-demand**: `Tools > UdonSharpCE > Show Optimization Report`
- **Per-asset**: Select program asset, see report in Inspector

---

## Part 7: Implementation Plan

### Phase 1: Library Optimizations (Weeks 1-3)

**High priority, low risk, immediate benefit**:

| Task                          | Effort  | Impact                       |
| ----------------------------- | ------- | ---------------------------- |
| Hash index fix (13 locations) | 2 hours | All Dictionary/HashSet users |
| CEDictionary tombstone fix    | 4 hours | Correctness + performance    |
| CEWorld pending destruction   | 4 hours | All ECS users                |
| CEQuery hot path optimization | 6 hours | All ECS users                |
| Collection unchecked access   | 4 hours | Power users                  |
| Iterator allocation reduction | 6 hours | All collection users         |
| CEPool handle API             | 4 hours | All pool users               |

**Status:** ✅ Complete (all library optimizations shipped)

**Checklist:**

- [x] Hash index mask fix across Dictionary/HashSet (12 locations: 8 in Dictionary, 4 in HashSet)
- [x] CEDictionary tombstone fix (SlotEmpty/SlotOccupied/SlotTombstone states, threshold-based rehash)
- [x] CEWorld pending destruction pending-list flow
- [x] CEQuery direct-array hot paths
- [x] Collection unchecked/backing-array access helpers (List, CEList, Queue, Stack)
- [x] Iterator allocation reduction + cached iterators (List, CEList, Queue, Stack)
- [x] CEPool handle-based O(1) release API (PoolHandle<T>, AcquireHandle, Release(handle))
- [x] Iterator caching for CEList (matches List/Queue/Stack pattern)

**Deliverable**: CE library update — **COMPLETE**.

### Phase 2: Automatic Compiler Optimizations (Weeks 4-7)

| Task                           | Effort | Impact     |
| ------------------------------ | ------ | ---------- |
| Roslyn analyzer infrastructure | 1 week | Foundation |
| Constant folding               | 3 days | All users  |
| Dead code elimination          | 3 days | All users  |
| String interning               | 3 days | All users  |
| Small loop unrolling           | 2 days | All users  |
| Tiny method inlining           | 3 days | All users  |

**Status:** Not started (compiler work remains proposal-stage)

**Checklist:**

- [ ] Roslyn analyzer infrastructure
- [ ] Constant folding
- [ ] Dead code elimination
- [ ] String interning
- [ ] Small loop unrolling
- [ ] Tiny method inlining

**Deliverable**: Automatic optimizations enabled by default.

### Phase 3: Smart Default Optimizations (Weeks 8-11)

| Task                 | Effort | Impact                 |
| -------------------- | ------ | ---------------------- |
| Auto sync packing    | 1 week | Network-heavy worlds   |
| Auto delta sync      | 1 week | Position/rotation sync |
| Disable attributes   | 2 days | Edge cases             |
| Optimization reports | 3 days | Developer visibility   |

**Status:** Not started (blocked on compiler foundations)

**Checklist:**

- [ ] Auto sync packing
- [ ] Auto delta sync
- [ ] Disable/escape hatch attributes
- [ ] Optimization reports surfaced in editor/CI

**Deliverable**: Smart defaults with escape hatches.

### Phase 4: Opt-in Features (Weeks 12-16)

| Task                  | Effort  | Impact              |
| --------------------- | ------- | ------------------- |
| [CEPooled] generation | 2 weeks | Object-heavy worlds |
| [CECompressSync]      | 1 week  | Bandwidth-critical  |
| [CEBatchEvents]       | 3 days  | Event-heavy worlds  |
| [CEDebugOnly]         | 2 days  | Debug code          |

**Status:** Not started (awaiting completion of Phase 2/3)

**Checklist:**

- [ ] [CEPooled] generation
- [ ] [CECompressSync]
- [ ] [CEBatchEvents]
- [ ] [CEDebugOnly]

**Deliverable**: Full opt-in feature set.

### Phase 5: Testing & Polish (Weeks 17-20)

**Status:** Not started (dependent on Phases 2-4)

**Checklist:**

- [ ] Comprehensive test suite
- [ ] Real-world validation with partner creators
- [ ] Documentation
- [ ] Performance benchmarks

---

## Current Progress (Dec 2025)

- **Phase 1 (library) — ✅ Complete:** All library optimizations shipped:
  - CEWorld pending-destroy list (O(1) early exit when no pending)
  - CEQuery direct-array hot paths (no method calls in iteration)
  - CEPool handle-based O(1) release (PoolHandle<T> struct)
  - CEDictionary tombstone deletion (SlotEmpty/SlotOccupied/SlotTombstone states, threshold-based rehash)
  - Dictionary/HashSet bitwise hash masks (`& 0x7FFFFFFF` instead of `Mathf.Abs`)
  - Unchecked/backing-array access for List, CEList, Queue, Stack
  - Allocation-free ForEach iteration methods
  - Cached iterators to avoid foreach allocations
- **Phase 2 (automatic compiler) — Not started:** Proposal-only; constant folding, DCE, string interning, tiny inlining, small-loop unroll still to implement in the compiler pipeline.
- **Phase 3 (smart defaults) — Not started:** Auto sync packing/delta sync, disable attributes, and optimization reports queued behind compiler foundation.
- **Phase 4 (opt-in features) — Not started:** [CEPooled], [CECompressSync], [CEBatchEvents], [CEDebugOnly] pending after smart defaults.
- **Phase 5 (testing & polish) — Not started:** Comprehensive tests, partner validation, documentation, and perf benchmarks to follow feature delivery.
- **Validation harness**: Still to wire up golden assembly diffs and CI microbenchmarks described in Part 9.

---

## Part 8: Compatibility

### Backward Compatibility

| Scenario                  | Behavior                                   |
| ------------------------- | ------------------------------------------ |
| No CE attributes          | Code compiles with automatic optimizations |
| Existing projects         | Work unchanged, gain automatic benefits    |
| Disable all optimizations | `[CENoOptimize]` on class                  |

### Forward Compatibility

CE optimizations designed for stability:

- Library changes are internal implementation details
- Compiler optimizations preserve semantics
- Generated code compatible with future UdonSharp versions

### Interoperability

| Tool             | Compatibility               |
| ---------------- | --------------------------- |
| UdonSharp 1.x    | ✅ Required base            |
| VRCFury          | ✅ No conflicts             |
| ClientSim        | ✅ Optimized code runs same |
| Existing prefabs | ✅ Work unchanged           |

---

## Part 9: Validation, Rollout, and Risk Management

### 9.1 Benchmark & Validation Plan

| Area        | Scenario                                                                                              | Success Criteria                                                               |
| ----------- | ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ |
| Collections | 100k operations per method (`Add`, `Contains`, `Remove`) on `List`, `Dictionary`, `HashSet`, `CEPool` | ≥20% speedup vs baseline, zero behavior regressions                            |
| ECS         | CEWorld with 1k / 5k entities, 3-component queries, 30% churn                                         | ≥30% faster query iteration, pending-destroy path costs ~0 in idle case        |
| Sync        | 32 small sync fields, 8 bools, 4 Vector3/Quaternion under auto-pack/delta                             | ≥50% bandwidth reduction, no desync or callback regressions                    |
| Compiler    | Golden C# fixtures compiled with and without CE transforms                                            | Bytecode identical for disabled features; optimized variants match expected IR |
| Memory      | Object pool acquire/release under churn (10k ops)                                                     | ≤60% allocations vs baseline; no leaks                                         |

Validation harness:

- **Editor**: Deterministic playmode tests using CE.DevTools profilers (frame timing + allocation counters).
- **Headless**: CLI microbenchmarks run via CI to produce before/after CSVs.
- **Network**: Bandwidth Analyzer scenes with simulated 200ms latency / 5% loss to verify delta/packing wins and stability.

### 9.2 Regression Safety Nets

- **Golden Compilation Tests**: Snapshot IL/Udon assembly for representative scripts; diff to detect unintended rewrites.
- **Analyzer Coverage**: Unit tests for Roslyn rules that trigger sync packing/delta sync/inlining to avoid false positives.
- **Runtime Canaries**: Optional `[CERuntimeAssert]` helper (editor-only) to verify collection invariants (dictionary tombstones, pool occupancy).
- **Fallback Switches**: Global project setting `CE Runtime Optimizations: Off/Auto/Strict` (defaults to Auto) and per-class `[CENoOptimize]`.
- **Shadow Mode**: Editor toggle to run both optimized and unoptimized sync serialization in parallel and compare payload hashes (dev-only).

### 9.3 Rollout Strategy

1. **Phase 1 (Weeks 1-3)** — ✅ Library changes shipped and enabled by default.
2. **Phase 2 (Weeks 4-7)** — Compiler automatic passes enabled; reports surface applied rewrites; fallback switch documented.
3. **Phase 3 (Weeks 8-11)** — Smart defaults (packing/delta) ship disabled in CI until green on benchmark + partner worlds, then enabled by default.
4. **Phase 4+** — Opt-in attributes released with samples; no behavioural change unless attribute present.

Success gates to advance phases:

- No correctness regressions in golden scenes (functional tests + late-join).
- Benchmarks meet success criteria in 9.1.
- Partner world sign-off: build + client test with CE enabled.

### 9.4 Risks & Mitigations

| Risk                                                   | Impact                                           | Mitigation                                                                                          |
| ------------------------------------------------------ | ------------------------------------------------ | --------------------------------------------------------------------------------------------------- |
| Sync packing breaks `FieldChangeCallback` expectations | Missed callbacks or unexpected grouping          | Auto-disable packing when callback detected; document `[CENoPackSync]`                              |
| Delta sync hides intentional micro-movements           | Visual drift or stale state                      | Adjustable thresholds; `[CEDeltaSync(false)]` escape hatch; per-variable overrides                  |
| Tombstone fix changes iteration order                  | Subtle behavior change for code relying on order | Document non-ordered semantics; add analyzer warning for order-dependent usage patterns             |
| Inlining/unrolling increases code size                 | Larger assembly may hit limits                   | Cap unroll iterations; skip on large bodies; report code size deltas                                |
| Pool handle misuse (releasing invalid handle)          | Silent object loss                               | Handle validity checks in debug builds; optional runtime asserts; keep legacy API for compatibility |
| Cross-version prefabs with official UdonSharp          | Install/compile conflicts                        | VPM `conflicts` + `provides` metadata; README warning; automated validator in DevTools              |

### 9.5 Owner & Checkpoints

- **Perf Lead:** CE.Perf maintainer (owns library changes + ECS benchmarks)
- **Compiler Lead:** CE compiler maintainer (owns Roslyn passes + golden tests)
- **QA/Validation:** DevTools maintainer (owns harnesses + reports)
- **Checkpoints:** Weekly perf report (ops/sec, bandwidth, allocations), plus milestone demos at end of each phase.

---

## Appendix A: Quick Reference

### What Happens Automatically

| Optimization         | Trigger                  | Benefit                     |
| -------------------- | ------------------------ | --------------------------- |
| Hash index fix       | Always                   | Faster Dictionary/HashSet   |
| CEWorld pending list | Always                   | Faster when no destructions |
| CEQuery inlining     | Always                   | Faster ECS queries          |
| Constant folding     | Constant expressions     | Fewer instructions          |
| Dead code removal    | Unreachable code         | Smaller programs            |
| String interning     | Duplicate strings        | Less memory                 |
| Loop unrolling       | Small fixed loops        | Less overhead               |
| Method inlining      | Tiny methods             | Fewer calls                 |
| Sync packing         | Adjacent small sync vars | Less bandwidth              |
| Delta sync           | Vector3/Quaternion sync  | Less bandwidth              |

### What Requires Attributes

| Feature          | Attribute              | When to Use               |
| ---------------- | ---------------------- | ------------------------- |
| Object pooling   | `[CEPooled]`           | Many instantiate/destroy  |
| Sync compression | `[CECompressSync]`     | Bandwidth-critical floats |
| Event batching   | `[CEBatchEvents]`      | Many events at once       |
| Debug stripping  | `[CEDebugOnly]`        | Debug-only code           |
| Disable packing  | `[CENoPackSync]`       | Need individual callbacks |
| Disable delta    | `[CEDeltaSync(false)]` | Need every update         |
| Disable all      | `[CENoOptimize]`       | Exact control needed      |

---

## Appendix B: Benchmark Expectations

### Library Optimizations

| Operation                          | Before             | After                 | Speedup             |
| ---------------------------------- | ------------------ | --------------------- | ------------------- |
| Dictionary.Add                     | 1.0x               | 1.2x                  | 20%                 |
| Dictionary.TryGetValue             | 1.0x               | 1.25x                 | 25%                 |
| Dictionary.Remove                  | 1.0x (O(n) rehash) | 2-5x (O(1) tombstone) | 100-400%            |
| HashSet.Contains                   | 1.0x               | 1.2x                  | 20%                 |
| CEQuery.ForEach (1000 entities)    | 1.0x               | 1.5x                  | 50%                 |
| CEWorld.ProcessPending (0 pending) | 1.0x               | 100x+                 | 10000%+             |
| CEPool.Release                     | 1.0x (O(n))        | 10x+ (O(1))           | 1000%+              |
| List foreach                       | 1.0x               | 1.3x                  | 30% (no allocation) |

### Compiler Optimizations

| Scenario                  | Bandwidth | Instructions |
| ------------------------- | --------- | ------------ |
| 4 byte sync vars          | -75%      | -            |
| 8 bool sync vars          | -87.5%    | -            |
| Vector3 + Quaternion sync | -40-60%   | -            |
| Typical script            | -         | -15-25%      |

---

## Appendix C: Phase 1 API Reference

These APIs are shipped and available for use.

### CEPool<T> — O(1) Release API

```csharp
// Struct returned by AcquireHandle() for O(1) release
public struct PoolHandle<T> where T : class
{
    public readonly int Index;      // Pool index
    public readonly T Object;       // The pooled object
    public bool IsValid { get; }    // True if handle is valid
    public static PoolHandle<T> Invalid { get; }  // Invalid handle constant
}

// CEPool<T> methods
PoolHandle<T> AcquireHandle();     // Returns handle for O(1) release
bool Release(PoolHandle<T> handle); // O(1) release by handle
bool Release(T obj);               // O(n) release by object (legacy)
bool ReleaseByIndex(int index);    // O(1) release by known index
```

**Usage Example:**

```csharp
// Recommended: O(1) release pattern
var handle = bulletPool.AcquireHandle();
if (handle.IsValid)
{
    Bullet bullet = handle.Object;
    bullet.Fire(direction);

    // Later: O(1) release
    bulletPool.Release(handle);
}
```

### Collection Unchecked Access

Available on `List<T>`, `CEList<T>`, `Queue<T>`, `Stack<T>`:

```csharp
T GetUnchecked(int index);          // No bounds check
void SetUnchecked(int index, T v);  // No bounds check
T[] GetBackingArray();              // Direct array access
```

**Usage Example:**

```csharp
// Hot loop with guaranteed bounds
var enemies = GetEnemies();
int count = enemies.Count;
for (int i = 0; i < count; i++)
{
    enemies.GetUnchecked(i).Update();  // 10-20% faster
}
```

### Allocation-Free Iteration

Available on `List<T>`, `CEList<T>`, `Queue<T>`, `Stack<T>`:

```csharp
void ForEach(Action<T> action);              // Iterate all items
void ForEachWithIndex(Action<T, int> action); // With index
void ForEachUntil(Func<T, bool> predicate);  // Until predicate false
```

**Usage Example:**

```csharp
// No allocation (unlike foreach)
enemies.ForEach(e => e.TakeDamage(10));

// With early exit
enemies.ForEachUntil(e => {
    if (e.IsDead) return false;  // Stop iteration
    e.Update();
    return true;
});
```

### CEDictionary<TKey, TValue> — Tombstone Deletion

Tombstone deletion is automatic. `Remove()` is now O(1) amortized instead of O(n):

```csharp
bool Remove(TKey key);  // Now O(1) amortized with tombstones
```

**Internal Implementation:**

- Deleted slots marked as tombstones (not empty)
- Lookups continue past tombstones
- Automatic rehash when tombstones exceed 25% of capacity
- Tombstones cleared during rehash

### CEQuery — Direct Array Hot Paths

Query iteration is automatically optimized. No API changes needed:

```csharp
// All these are faster due to internal direct array access
int count = query.Count();
int firstId = query.First();
int matchCount = query.Execute(resultArray);
query.ForEach(id => ProcessEntity(id));
```

### CEWorld — Pending Destruction Optimization

Deferred destruction is automatically optimized. No API changes needed:

```csharp
// Still call the same API
world.DestroyEntityDeferred(entityId);

// ProcessPendingDestructions() is now O(1) when no pending
// Previously was O(n) scanning all entities every tick
```

---

## Appendix D: Migration Notes

### From Pre-CE Collections

If migrating from standard UdonSharp collections to CE collections:

| Old Pattern               | New Pattern              | Benefit         |
| ------------------------- | ------------------------ | --------------- |
| `foreach (var x in list)` | `list.ForEach(x => ...)` | No allocation   |
| `list[i]` in hot loops    | `list.GetUnchecked(i)`   | No bounds check |
| `pool.Release(obj)`       | `pool.Release(handle)`   | O(1) vs O(n)    |

### Backward Compatibility

All changes are backward compatible:

- Old `foreach` loops still work (with allocation)
- Old `Release(obj)` still works (with O(n) search)
- Old indexed access still works (with bounds check)

No code changes required to benefit from internal optimizations (hash index fix, tombstone deletion, pending destruction list, query hot paths).

---

_Proposal End_
