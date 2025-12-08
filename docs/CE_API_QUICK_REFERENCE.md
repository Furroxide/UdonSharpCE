# UdonSharpCE API Quick Reference

A condensed reference for CE APIs. For detailed documentation, see `AGENTS.md`.

---

## Namespaces

```csharp
using UdonSharp.Lib.Internal.Collections;  // List, Dictionary, HashSet, Queue, Stack
using UdonSharp.CE.Perf;                   // CEPool, CEWorld, CEQuery
using UdonSharp.CE.Async;                  // UdonTask
using UdonSharp.CE.DevTools;               // CELogger
using UdonSharp.CE.Net;                    // RpcAttribute
using UdonSharp.CE.Persistence;            // CEPersistence
```

---

## Collections

### List<T>

```csharp
List<T> list = new List<T>(capacity);

// Add/Remove
list.Add(item);                    // O(1) amortized
list.Insert(index, item);          // O(n)
list.Remove(item);                 // O(n) - first occurrence
list.RemoveAt(index);              // O(n)
list.RemoveRange(index, count);    // O(n)
list.Clear();                      // O(1)

// Access
T item = list[index];              // O(1) + bounds check
T item = list.GetUnchecked(index); // O(1) no bounds check
list.SetUnchecked(index, value);   // O(1) no bounds check
T[] array = list.GetBackingArray(); // Direct array access

// Query
int count = list.Count;
bool has = list.Contains(item);    // O(n)
int idx = list.IndexOf(item);      // O(n), -1 if not found

// Utilities
list.Sort();                       // In-place (requires IComparable<T>)
list.Reverse();                    // In-place
T[] arr = list.ToArray();          // New array copy

// Iteration (allocation-free)
list.ForEach(item => { });
list.ForEachWithIndex((item, i) => { });
list.ForEachUntil(item => item != null);  // Stop when returns false

// Static creation
List<T> list = List<T>.CreateFromArray(array);
List<T> list = List<T>.CreateFromHashSet(hashSet);
```

### Dictionary<TKey, TValue>

```csharp
Dictionary<K, V> dict = new Dictionary<K, V>();

// Add/Update
dict.Add(key, value);              // O(1), throws if exists
dict[key] = value;                 // O(1), add or update

// Access
V value = dict[key];               // O(1), throws if not found
bool found = dict.TryGetValue(key, out V value);  // O(1)

// Query
int count = dict.Count;
bool has = dict.ContainsKey(key);  // O(1)
bool has = dict.ContainsValue(val); // O(n)

// Remove
bool removed = dict.Remove(key);   // O(1)
bool removed = dict.Remove(key, out V value);

// Utilities
dict.Clear();

// Iteration
var enumerator = dict.GetEnumerator();
while (enumerator.MoveNext())
{
    K key = enumerator.Current.Key;
    V val = enumerator.Current.Value;
}
```

### HashSet<T>

```csharp
HashSet<T> set = new HashSet<T>();

// Add/Remove
bool added = set.Add(item);        // O(1), false if exists
bool removed = set.Remove(item);   // O(1)
set.Clear();

// Query
int count = set.Count;
bool has = set.Contains(item);     // O(1)

// Set Operations
set.UnionWith(other);              // Add all from other
set.IntersectWith(other);          // Keep only common
set.ExceptWith(other);             // Remove all in other
set.SymmetricExceptWith(other);    // Keep unique to each

// Set Comparisons
bool is = set.IsSubsetOf(other);
bool is = set.IsSupersetOf(other);
bool is = set.IsProperSubsetOf(other);
bool is = set.IsProperSupersetOf(other);
bool overlaps = set.Overlaps(other);
bool equals = set.SetEquals(other);

// Utilities
T[] arr = set.ToArray();
HashSet<T> set = HashSet<T>.CreateFromArray(array);
```

### Queue<T> & Stack<T>

```csharp
// Queue (FIFO)
Queue<T> queue = new Queue<T>(capacity);
queue.Enqueue(item);
T item = queue.Dequeue();
T item = queue.Peek();
int count = queue.Count;

// Stack (LIFO)
Stack<T> stack = new Stack<T>(capacity);
stack.Push(item);
T item = stack.Pop();
T item = stack.Peek();
int count = stack.Count;
```

---

## Object Pooling (CEPool)

```csharp
using UdonSharp.CE.Perf;

// Create pool
CEPool<T> pool = new CEPool<T>(capacity);

// Initialize
pool.Initialize(
    factory: (int index) => CreateObject(index),
    onAcquire: (T obj) => obj.SetActive(true),
    onRelease: (T obj) => obj.SetActive(false)
);

// Or initialize with existing array
pool.InitializeWithArray(objects, onAcquire, onRelease);

// Acquire (returns null if pool exhausted)
T obj = pool.Acquire();
T obj = pool.AcquireWithIndex(out int index);

// Acquire with handle (PREFERRED - O(1) release)
PoolHandle<T> handle = pool.AcquireHandle();
if (handle.IsValid)
{
    T obj = handle.Object;
    int idx = handle.Index;
}

// Release
pool.Release(handle);              // O(1) with handle
pool.Release(obj);                 // O(n) without handle - AVOID
pool.ReleaseByIndex(index);        // O(1)
pool.ReleaseAll();                 // Release everything

// State
int cap = pool.Capacity;
int active = pool.ActiveCount;
int available = pool.AvailableCount;
bool full = pool.IsFull;
bool empty = pool.IsEmpty;
bool init = pool.IsInitialized;

// Utilities
T obj = pool.GetByIndex(index);
bool inUse = pool.IsInUse(index);
int count = pool.GetActiveObjects(resultArray);
int count = pool.GetActiveIndices(resultArray);
pool.ForEachActive(obj => { });
pool.ForEachActiveWithIndex((obj, i) => { });
```

---

## ECS (CEWorld + CEQuery)

```csharp
using UdonSharp.CE.Perf;

// Create world
CEWorld world = new CEWorld(maxEntities);  // Default 1000

// Register component arrays
int slot = world.RegisterComponent(typeId, componentArray);

// Entity Management
int id = world.CreateEntity();             // Returns InvalidEntity (-1) if full
world.DestroyEntity(id);                   // Immediate
world.DestroyEntityDeferred(id);           // At end of Tick() - safe during iteration
world.EnableEntity(id);
world.DisableEntity(id);
bool valid = world.IsValidEntity(id);
EntityState state = world.GetEntityState(id);
int version = world.GetEntityVersion(id);

// Component Management
world.AddComponent(entityId, componentSlot);    // Sets bitmask bit
world.RemoveComponent(entityId, componentSlot); // Clears bitmask bit
bool has = world.HasComponent(entityId, componentSlot);
int mask = world.GetComponentMask(entityId);
object arr = world.GetComponentArray(slot);
int slot = world.GetComponentSlot(typeId);

// System Registration
int sysId = world.RegisterSystem(action, order);  // Lower order runs first
world.EnableSystem(sysId);
world.DisableSystem(sysId);
bool enabled = world.IsSystemEnabled(sysId);

// Update (call in Update())
world.Tick();

// World State
int max = world.MaxEntities;
int active = world.ActiveEntityCount;
int compTypes = world.ComponentTypeCount;
int sysCount = world.SystemCount;

// Utilities
world.ClearEntities();
int count = world.GetActiveEntities(resultArray);
int count = world.CountEntitiesWithMask(requiredMask);
int count = world.GetEntitiesWithMask(requiredMask, resultArray);

// EntityState enum
EntityState.Free            // Slot not in use
EntityState.Active          // Active entity
EntityState.Disabled        // Disabled (skipped by queries)
EntityState.PendingDestroy  // Marked for destruction
```

### CEQuery

```csharp
// Build query
CEQuery query = new CEQuery(world)
    .With(componentSlot)      // Must have
    .Without(componentSlot);  // Must NOT have

// Reset query
query.Reset();

// Execute
bool matches = query.Matches(entityId);
int count = query.Count();
int first = query.First();        // InvalidEntity if none
bool any = query.Any();
int found = query.Execute(resultArray);

// Iteration (allocation-free)
query.ForEach(entityId => { });
query.ForEachWhile(entityId => continueIteration);  // Return false to stop

// Cached iteration (refresh once per frame)
query.RefreshCache(maxResults);
int cachedCount = query.CachedCount;
int entityId = query.GetCached(index);
query.ForEachCached(entityId => { });

// Properties
int required = query.RequiredMask;
int excluded = query.ExcludedMask;
```

---

## Async (UdonTask)

```csharp
using UdonSharp.CE.Async;

// Async method
public async UdonTask DoSomething()
{
    await UdonTask.Delay(1.5f);       // Delay seconds
    await UdonTask.DelayFrames(5);    // Delay frames
    await UdonTask.Yield();           // Next frame
    
    await UdonTask.WhenAll(task1, task2);  // Wait for all
    await UdonTask.WhenAny(task1, task2);  // Wait for first
}

// Task status
bool done = task.IsCompleted;
bool success = task.IsCompletedSuccessfully;
bool canceled = task.IsCanceled;
bool faulted = task.IsFaulted;
string error = task.Error;
TaskStatus status = task.Status;

// Static creators
UdonTask.CompletedTask;
UdonTask.FromCanceled();
UdonTask.FromError("message");

// TaskStatus enum
TaskStatus.Created
TaskStatus.WaitingForActivation
TaskStatus.Running
TaskStatus.RanToCompletion
TaskStatus.Canceled
TaskStatus.Faulted
```

---

## Logging (CELogger)

```csharp
using UdonSharp.CE.DevTools;

// Basic logging
CELogger.Trace("message");
CELogger.Debug("message");
CELogger.Info("message");
CELogger.Warning("message");
CELogger.Error("message");

// With tag
CELogger.Info("Tag", "message");
CELogger.Error("Network", "Connection failed");

// With level
CELogger.Log("message", LogLevel.Info);
CELogger.Log("Tag", "message", LogLevel.Warning);

// Configuration
CELogger.MinLevel = LogLevel.Warning;  // Filter by level
CELogger.OutputToUnityLog = false;     // Disable Unity console

// Utility
CELogger.ClearBuffer();
int count = CELogger.BufferCount;

// LogLevel enum
LogLevel.Trace
LogLevel.Debug
LogLevel.Info
LogLevel.Warning
LogLevel.Error
```

---

## Networking Attributes

```csharp
using UdonSharp.CE.Net;

// RPC attribute
[Rpc(Target = RpcTarget.All)]
[Rpc(Target = RpcTarget.All, RateLimit = 5f)]
[Rpc(Target = RpcTarget.All, OwnerOnly = true)]
[Rpc(Target = RpcTarget.All, DropOnRateLimit = true)]
public void MyRpc() { }

// Shorthand for owner-only
[RpcOwnerOnly]
public void OwnerOnlyRpc() { }

// RpcTarget enum
RpcTarget.All
RpcTarget.Owner
```

---

## Persistence (CEPersistence)

```csharp
using UdonSharp.CE.Persistence;
using VRC.SDK3.Data;

// Register model
CEPersistence.Register<T>(
    toData: model => new DataDictionary { ["key"] = model.field },
    fromData: data => new T { field = (int)data["key"].Double },
    key: "storage_key",
    version: 1,
    validate: model => new List<ValidationError>()
);

// Check registration
bool reg = CEPersistence.IsRegistered<T>();
string key = CEPersistence.GetKey<T>();
int ver = CEPersistence.GetVersion<T>();

// Save
SaveResult result = CEPersistence.Save(model);

// Restore
RestoreResult result = CEPersistence.Restore(out T model);

// Conversion
DataDictionary data = CEPersistence.ToData(model);
string json = CEPersistence.ToJson(model, beautify: true);
RestoreResult result = CEPersistence.FromData(data, out T model);
RestoreResult result = CEPersistence.FromJson(json, out T model);

// Validation
bool valid = CEPersistence.Validate(model, out List<ValidationError> errors);

// Size estimation
int size = CEPersistence.EstimateSize(model);
int remaining = CEPersistence.GetRemainingQuota();

// Constants
CEPersistence.PLAYER_DATA_QUOTA   // 100KB
CEPersistence.PLAYER_OBJECT_QUOTA // 100KB

// SaveResult enum
SaveResult.Success
SaveResult.NotRegistered
SaveResult.ValidationFailed
SaveResult.QuotaExceeded
SaveResult.NotAllowed
SaveResult.NetworkError

// RestoreResult enum
RestoreResult.Success
RestoreResult.NoData
RestoreResult.ParseError
RestoreResult.VersionMismatch
RestoreResult.NotReady
RestoreResult.NetworkError
```

---

## VRChat Sync Patterns

```csharp
// Manual sync behaviour
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SyncedBehaviour : UdonSharpBehaviour
{
    [UdonSynced] private int _value;
    
    public void SetValue(int v)
    {
        // 1. Check/take ownership
        if (!Networking.IsOwner(gameObject))
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }
        
        // 2. Modify
        _value = v;
        
        // 3. Request sync
        RequestSerialization();
    }
    
    // 4. Handle remote updates
    public override void OnDeserialization()
    {
        UpdateUI();
    }
}

// Network events
SendCustomNetworkEvent(NetworkEventTarget.All, nameof(OnEvent));
SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(OnOwnerEvent));
```

---

## Udon Constraints Reminder

| ❌ NOT Available | ✅ Use Instead |
|------------------|----------------|
| `System.Collections.Generic` | CE Collections |
| `async`/`await` (standard) | `UdonTask` |
| LINQ | Manual loops |
| Reflection | Direct references |
| `dynamic` | Concrete types |
| Multi-threading | Single thread |
| Delegates as fields | Method names |

---

## Performance Tips

```csharp
// ✅ DO
list.GetUnchecked(i);              // No bounds check
pool.Release(handle);              // O(1) with handle
query.ForEach(id => { });          // Allocation-free

// ❌ AVOID
foreach (var x in list) { }        // Allocates iterator
pool.Release(obj);                 // O(n) search
GetComponent<T>() in Update;       // Slow, cache in Start
string + string in loop;           // Allocates
Instantiate/Destroy frequently;    // Use pooling
```

---

*Version: UdonSharpCE (com.merlin.UdonSharp) | Unity 2022.3.22f1 | VRCSDK 3.7.x*

