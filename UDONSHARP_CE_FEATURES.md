# UdonSharp CE — Features, Differences & Additions

_A Comprehensive Reference for UdonSharp Community Edition_

**Version:** 1.0  
**Last Updated:** December 2025

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Module Overview](#2-module-overview)
3. [Feature Comparison Tables](#3-feature-comparison-tables)
4. [Detailed Feature Documentation](#4-detailed-feature-documentation)
   - [CE.Data](#41-cedata--ergonomic-data-layer)
   - [CE.Async](#42-ceasync--coroutine--task-system)
   - [CE.Net](#43-cenet--typed-networking-layer)
   - [CE.Persistence](#44-cepersistence--orm-style-data-mapping)
   - [CE.DevTools](#45-cedevtools--development--debugging)
   - [CE.Perf](#46-ceperf--performance-framework)
   - [CE.Procgen](#47-ceprocgen--procedural-generation)
   - [CE.GraphBridge](#48-cegraphbridge--visual-scripting-integration)
5. [Runtime Optimizations](#5-runtime-optimizations)
6. [Compile-Time Analyzers](#6-compile-time-analyzers)
7. [Editor Tools](#7-editor-tools)
8. [Inspector System](#8-inspector-system)
9. [VPM Distribution](#9-vpm-distribution)
10. [Known Limitations & Pitfalls](#10-known-limitations--pitfalls)
11. [Migration Guide](#11-migration-guide)
12. [API Quick Reference](#12-api-quick-reference)

---

## 1. Executive Summary

### What is UdonSharp CE?

**UdonSharp Community Edition (CE)** is an enhanced fork of MerlinVR's UdonSharp that builds on the 1.2-beta1 release to provide a cohesive, modular framework for building ambitious VRChat worlds. Rather than chasing full C# parity, UdonSharpCE focuses on **raising the abstraction level** where it matters most:

- **Data Management** — Type-safe collections and data bridges
- **Asynchronous Workflows** — Async/await patterns compiled to state machines
- **Networking** — Typed sync and RPC with compile-time analysis
- **Persistence** — ORM-style mapping to VRChat's data systems
- **Performance** — ECS-Lite, pooling, and batching for high-entity worlds
- **Procedural Content** — Deterministic generation across all clients

### Baseline: Merlin's UdonSharp 1.2-beta1

UdonSharpCE treats **Merlin's 1.2-beta1** as the minimum baseline. This provides:

| Feature                                                                                  | Status      |
| ---------------------------------------------------------------------------------------- | ----------- |
| Non-UdonSharpBehaviour class support                                                     | ✅ Included |
| Generic types                                                                            | ✅ Included |
| Built-in collections: `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>` | ✅ Included |
| Operator overloading                                                                     | ✅ Included |
| Custom type serialization (persistence-compatible)                                       | ✅ Included |

**Known limitations inherited from 1.2-beta1:**

| Limitation                            | Status                         |
| ------------------------------------- | ------------------------------ |
| No inheritance on non-behaviour types | ❌ Not supported               |
| No native struct support              | ❌ Workarounds used            |
| No true static fields                 | ❌ Emulated per-behaviour-type |

### Key Differentiators from Official SDK UdonSharp

| Capability                  | Official SDK  | Merlin 1.2-beta1 | UdonSharp CE |
| --------------------------- | ------------- | ---------------- | ------------ |
| Generic types               | ❌            | ✅               | ✅           |
| Non-behaviour classes       | ❌            | ✅               | ✅           |
| Built-in collections        | ❌            | ✅               | ✅ Enhanced  |
| Async/await syntax          | ❌            | ❌               | ✅           |
| ECS-Lite framework          | ❌            | ❌               | ✅           |
| Typed networking attributes | ❌            | ❌               | ✅           |
| Persistence ORM             | ❌            | ❌               | ✅           |
| Procedural generation       | ❌            | ❌               | ✅           |
| Compile-time analyzers      | ❌            | ❌               | ✅           |
| Editor debugging tools      | ❌            | ❌               | ✅           |
| VPM distribution            | ❌ (embedded) | ❌ (manual)      | ✅           |

---

## 2. Module Overview

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        UdonSharpCE                              │
├─────────────┬─────────────┬─────────────┬─────────────┬─────────┤
│   CE.Data   │  CE.Async   │   CE.Net    │CE.Persistence│CE.Graph │
│             │             │             │             │ Bridge  │
├─────────────┴─────────────┴─────────────┴─────────────┴─────────┤
│                         CE.DevTools                             │
├─────────────────────────────────────────────────────────────────┤
│          CE.Perf                    │         CE.Procgen        │
│    (ECS-Lite, Pooling, Batching)    │  (Deterministic Gen, WFC) │
└─────────────────────────────────────────────────────────────────┘
```

### Module Summary

| Module             | Purpose                                                             | Status      |
| ------------------ | ------------------------------------------------------------------- | ----------- |
| **CE.Data**        | Type-safe data abstractions bridging collections to Data Containers | ✅ Complete |
| **CE.Async**       | Async/await workflows compiled to state machines                    | ✅ Complete |
| **CE.Net**         | Typed RPC and sync with compile-time analysis                       | ✅ Complete |
| **CE.Persistence** | Attribute-based mapping to PlayerData/PlayerObject                  | ✅ Complete |
| **CE.DevTools**    | Debug console, profiler, analyzers, editor tools                    | ✅ Complete |
| **CE.Perf**        | ECS-Lite, object pooling, spatial partitioning                      | ✅ Complete |
| **CE.Procgen**     | Deterministic procedural generation                                 | ✅ Complete |
| **CE.GraphBridge** | Expose CE systems to Udon Graph users                               | ✅ Complete |

### VPM Package Reference

| Package ID                            | Display Name                | Dependencies      |
| ------------------------------------- | --------------------------- | ----------------- |
| `com.charlvs.udonsharpce`             | UdonSharp Community Edition | VRChat Worlds SDK |
| `com.charlvs.udonsharpce.devtools`    | CE.DevTools                 | Core              |
| `com.charlvs.udonsharpce.data`        | CE.Data                     | Core              |
| `com.charlvs.udonsharpce.persist`     | CE.Persistence              | Core, CE.Data     |
| `com.charlvs.udonsharpce.async`       | CE.Async                    | Core              |
| `com.charlvs.udonsharpce.net`         | CE.Net                      | Core              |
| `com.charlvs.udonsharpce.perf`        | CE.Perf                     | Core              |
| `com.charlvs.udonsharpce.procgen`     | CE.Procgen                  | Core, CE.Perf     |
| `com.charlvs.udonsharpce.graphbridge` | CE.GraphBridge              | Core              |

---

## 3. Feature Comparison Tables

### Language Features

| Feature              | Standard Udon | Official U#       | Merlin 1.2b1 | CE           |
| -------------------- | ------------- | ----------------- | ------------ | ------------ |
| C# syntax            | ❌            | ✅                | ✅           | ✅           |
| Classes              | ❌            | ✅ Behaviour only | ✅ Any class | ✅ Any class |
| Generics             | ❌            | ❌                | ✅           | ✅           |
| Generic constraints  | ❌            | ❌                | ✅           | ✅           |
| Operator overloading | ❌            | ❌                | ✅           | ✅           |
| Object initializers  | ❌            | ❌                | ❌           | ✅           |
| Async/await          | ❌            | ❌                | ❌           | ✅           |
| Extension methods    | ❌            | ✅                | ✅           | ✅           |
| Lambda expressions   | ❌            | ✅ Limited        | ✅ Limited   | ✅ Limited   |
| LINQ                 | ❌            | ❌                | ❌           | ❌           |

### Collections

| Collection              | Standard Udon | Official U# | Merlin 1.2b1 | CE          |
| ----------------------- | ------------- | ----------- | ------------ | ----------- |
| Arrays                  | ✅            | ✅          | ✅           | ✅          |
| `List<T>`               | ❌            | ❌          | ✅           | ✅ Enhanced |
| `Dictionary<K,V>`       | ❌            | ❌          | ✅           | ✅ Enhanced |
| `HashSet<T>`            | ❌            | ❌          | ✅           | ✅ Enhanced |
| `Queue<T>`              | ❌            | ❌          | ✅           | ✅ Enhanced |
| `Stack<T>`              | ❌            | ❌          | ✅           | ✅ Enhanced |
| `DataList` bridge       | ❌            | ❌          | ❌           | ✅          |
| `DataDictionary` bridge | ❌            | ❌          | ❌           | ✅          |

### Networking

| Feature                  | Standard Udon | Official U# | Merlin 1.2b1 | CE  |
| ------------------------ | ------------- | ----------- | ------------ | --- |
| `[UdonSynced]`           | ✅            | ✅          | ✅           | ✅  |
| `SendCustomNetworkEvent` | ✅            | ✅          | ✅           | ✅  |
| Typed `[Sync]` attribute | ❌            | ❌          | ❌           | ✅  |
| Typed `[Rpc]` attribute  | ❌            | ❌          | ❌           | ✅  |
| `[LocalOnly]` attribute  | ❌            | ❌          | ❌           | ✅  |
| Rate limiting            | ❌            | ❌          | ❌           | ✅  |
| Delta encoding hints     | ❌            | ❌          | ❌           | ✅  |
| Bandwidth analysis       | ❌            | ❌          | ❌           | ✅  |
| Late-join helpers        | ❌            | ❌          | ❌           | ✅  |

### Persistence

| Feature             | Standard Udon | Official U# | Merlin 1.2b1 | CE     |
| ------------------- | ------------- | ----------- | ------------ | ------ |
| PlayerData API      | ✅ Manual     | ✅ Manual   | ✅ Manual    | ✅ ORM |
| PlayerObject API    | ✅ Manual     | ✅ Manual   | ✅ Manual    | ✅ ORM |
| Attribute mapping   | ❌            | ❌          | ❌           | ✅     |
| Lifecycle callbacks | ❌            | ❌          | ❌           | ✅     |
| Size estimation     | ❌            | ❌          | ❌           | ✅     |
| Validation          | ❌            | ❌          | ❌           | ✅     |

### Performance Tools

| Feature                | Standard Udon | Official U# | Merlin 1.2b1 | CE          |
| ---------------------- | ------------- | ----------- | ------------ | ----------- |
| Object pooling         | ❌            | ❌          | ❌           | ✅ CEPool   |
| ECS framework          | ❌            | ❌          | ❌           | ✅ ECS-Lite |
| Spatial partitioning   | ❌            | ❌          | ❌           | ✅ CEGrid   |
| Batch processing       | ❌            | ❌          | ❌           | ✅          |
| Compile-time analyzers | ❌            | ❌          | ❌           | ✅          |

### Editor Tooling

| Tool                    | Standard Udon | Official U# | Merlin 1.2b1 | CE          |
| ----------------------- | ------------- | ----------- | ------------ | ----------- |
| In-world debug console  | ❌            | ❌          | ❌           | ✅          |
| Performance profiler    | ❌            | ❌          | ❌           | ✅          |
| Bandwidth analyzer      | ❌            | ❌          | ❌           | ✅          |
| World validator         | ❌            | ❌          | ❌           | ✅          |
| Network simulator       | ❌            | ❌          | ❌           | ✅          |
| Late-join simulator     | ❌            | ❌          | ❌           | ✅          |
| Custom inspector system | ❌            | ✅ Basic    | ✅ Basic     | ✅ Enhanced |

---

## 4. Detailed Feature Documentation

### 4.1 CE.Data — Ergonomic Data Layer

**Purpose:** Type-safe, ergonomic data abstractions bridging Merlin's collections to VRChat's Data Containers.

**Location:** `Runtime/Libraries/CE/Data`

#### Features

##### 1. Collection Bridges

Seamless conversion between generic collections and VRChat's DataList/DataDictionary:

```csharp
using UdonSharpCE.Data;

public class InventoryManager : UdonSharpBehaviour
{
    private CEList<int> itemIds = new CEList<int>();

    public void AddItem(int id)
    {
        itemIds.Add(id);
    }

    // Convert to DataList for persistence/networking
    public DataList ToDataList() => itemIds.AsDataList();

    // Load from DataList
    public void FromDataList(DataList data)
    {
        itemIds = CEList<int>.FromDataList(data);
    }
}
```

##### 2. Model Definitions

Attribute-based field mapping with validation:

```csharp
using UdonSharpCE.Data;

[DataModel]
public class InventoryItem
{
    [DataField("id")] public int itemId;
    [DataField("qty")] public int quantity;
    [DataField("meta")] public string metadata;
}
```

##### 3. Type-Safe DataToken Wrappers

```csharp
// Before (manual, error-prone)
DataDictionary dict = new DataDictionary();
dict["health"] = new DataToken(100);
int health = dict["health"].Int;  // Must remember type

// After (CE type-safe)
CEDictionary<string, int> stats = new CEDictionary<string, int>();
stats["health"] = 100;
int health = stats["health"];  // Type-safe
```

#### Classes

| Class                        | Description                                   |
| ---------------------------- | --------------------------------------------- |
| `CEList<T>`                  | Generic list with DataList bridge             |
| `CEDictionary<TKey, TValue>` | Generic dictionary with DataDictionary bridge |
| `CEDataBridge`               | Static conversion utilities                   |
| `DataTokenConverter`         | Type conversion helpers                       |
| `[DataModel]`                | Attribute for model classes                   |
| `[DataField]`                | Attribute for field mapping                   |

---

### 4.2 CE.Async — Coroutine & Task System

**Purpose:** Async/await-style workflows compiled into Udon-compatible state machines.

**Location:** `Runtime/Libraries/CE/Async`

#### Features

##### 1. UdonTask / UdonTask<T>

Lightweight promise-like structures for async operations:

```csharp
using UdonSharpCE.Async;

public class CutsceneController : UdonSharpBehaviour
{
    public async UdonTask PlayIntro()
    {
        await FadeScreen.ToBlack(1.0f);
        await dialogue.ShowText("Welcome, traveler...", 3.0f);
        await UdonTask.WhenAll(SpawnVillagers(), StartAmbientAudio());
    }
}
```

##### 2. Coordination Primitives

```csharp
// Delay execution
await UdonTask.Delay(2.0f);

// Yield to next frame
await UdonTask.Yield();

// Wait for multiple tasks
await UdonTask.WhenAll(task1, task2, task3);

// Wait for first completion
await UdonTask.WhenAny(task1, task2);
```

##### 3. Cancellation Support

```csharp
private CancellationToken _token;

public async UdonTask LongOperation()
{
    for (int i = 0; i < 100; i++)
    {
        if (_token.IsCancellationRequested)
            return;

        await ProcessChunk(i);
        await UdonTask.Yield();
    }
}

public void Cancel()
{
    _token.Cancel();
}
```

##### 4. Sequence Builder API

Fluent API for simpler use cases:

```csharp
CESequence.Create()
    .Then(() => Debug.Log("Step 1"))
    .Delay(1.0f)
    .Then(() => Debug.Log("Step 2"))
    .Delay(0.5f)
    .Then(() => Debug.Log("Done"))
    .Run(this);
```

#### How It Works

The CE compiler transforms `async` methods into state machines at compile time:

```csharp
// What you write:
public async UdonTask Example()
{
    DoA();
    await UdonTask.Delay(1f);
    DoB();
}

// What gets compiled (conceptually):
private int _example_state = 0;
public void Example()
{
    switch (_example_state)
    {
        case 0:
            DoA();
            _example_state = 1;
            SendCustomEventDelayedSeconds(nameof(_Example_Continue), 1f);
            break;
        case 1:
            DoB();
            _example_state = 0;
            break;
    }
}
```

#### Classes

| Class               | Description                                          |
| ------------------- | ---------------------------------------------------- |
| `UdonTask`          | Non-generic task for void async methods              |
| `UdonTask<T>`       | Generic task with return value                       |
| `TaskStatus`        | Enum: Created, Running, Completed, Faulted, Canceled |
| `CancellationToken` | Cooperative cancellation support                     |

---

### 4.3 CE.Net — Typed Networking Layer

**Purpose:** Type-safe RPC and sync with compile-time analysis.

**Location:** `Runtime/Libraries/CE/Net`

#### Features

##### 1. Visibility Attributes

```csharp
using UdonSharpCE.Net;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class GameController : UdonSharpBehaviour
{
    // Local-only method, analyzer warns if called via network
    [LocalOnly]
    private void PlayLocalEffect() { }

    // Typed RPC with target and rate limiting
    [Rpc(Target = RpcTarget.All, RateLimit = 5)]
    public void AnnounceGoal(int team, int scorerId)
    {
        // Called on all clients
    }

    // Owner-only RPC
    [RpcOwnerOnly]
    public void RequestOwnership()
    {
        // Only owner can receive this
    }
}
```

##### 2. Typed Sync Properties

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class PlayerState : UdonSharpBehaviour
{
    // Basic sync
    [Sync] public int health;

    // With delta encoding hint
    [Sync(DeltaEncode = true)]
    public int[] playerScores = new int[16];

    // With interpolation hint
    [Sync(Interpolation = InterpolationMode.Linear)]
    public Vector3 position;

    // With quantization hint
    [Sync(Quantization = 0.01f)]
    public float rotation;
}
```

##### 3. Late-Join Sync Helpers

```csharp
[SyncOnJoin]
public class WorldState : UdonSharpBehaviour
{
    [Sync] public int gamePhase;
    [Sync] public float[] teamScores = new float[4];

    // Called when a late-joiner needs state
    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (Networking.IsMaster)
        {
            // Automatically requests sync for [SyncOnJoin] behaviours
            RequestSerialization();
        }
    }
}
```

##### 4. Rate Limiting

```csharp
// Built-in rate limiter prevents network spam
[Rpc(RateLimit = 10)]  // Max 10 calls per second
public void FrequentUpdate(Vector3 pos) { }

// Manual rate limiting
private RateLimiter _limiter = new RateLimiter(5f); // 5 per second

void Update()
{
    if (_limiter.CanSend())
    {
        SendUpdate();
    }
}
```

##### 5. Conflict Resolution

```csharp
using UdonSharpCE.Net;

// Automatic conflict resolution for ownership races
[Sync]
public int sharedCounter;

public void IncrementCounter()
{
    // ConflictResolver handles ownership transfer timing
    ConflictResolver.ResolveWrite(this, nameof(sharedCounter), () =>
    {
        sharedCounter++;
        RequestSerialization();
    });
}
```

#### Attributes Reference

| Attribute        | Description                                         |
| ---------------- | --------------------------------------------------- |
| `[Sync]`         | Marks field for synchronization with optional hints |
| `[Rpc]`          | Marks method as RPC with target and rate limit      |
| `[RpcOwnerOnly]` | RPC only receivable by owner                        |
| `[LocalOnly]`    | Method should never be called via network           |
| `[SyncOnJoin]`   | Behaviour syncs state to late-joiners               |

#### RpcTarget Options

| Target             | Description                          |
| ------------------ | ------------------------------------ |
| `RpcTarget.All`    | Send to all clients including sender |
| `RpcTarget.Others` | Send to all clients except sender    |
| `RpcTarget.Owner`  | Send only to object owner            |
| `RpcTarget.Master` | Send only to instance master         |

---

### 4.4 CE.Persistence — ORM-Style Data Mapping

**Purpose:** Attribute-based mapping to VRChat's PlayerData and PlayerObject systems.

**Location:** `Runtime/Libraries/CE/Persistence`

#### Features

##### 1. PlayerData Mapping

```csharp
using UdonSharpCE.Persistence;

[PlayerData("rpg_save")]
public class PlayerSaveData : UdonSharpBehaviour
{
    [PersistKey("xp")] public int experience;
    [PersistKey("lvl")] public int level;
    [PersistKey("inv")] public int[] inventory = new int[50];
    [PersistKey("pos")] public Vector3 lastPosition;

    // Lifecycle callbacks
    public void OnDataRestored()
    {
        Debug.Log($"Welcome back! Level {level}");
    }

    public void OnDataSaved()
    {
        Debug.Log("Progress saved!");
    }

    public void OnDataCorrupted()
    {
        Debug.LogWarning("Save data corrupted, resetting...");
        ResetToDefaults();
    }
}
```

##### 2. Validation Constraints

```csharp
[PlayerData("settings")]
public class PlayerSettings : UdonSharpBehaviour
{
    [PersistKey("vol")]
    [Range(0f, 1f)]
    public float volume = 0.8f;

    [PersistKey("name")]
    [MaxLength(32)]
    public string displayName;

    [PersistKey("sensitivity")]
    [Range(0.1f, 5f)]
    public float mouseSensitivity = 1f;
}
```

##### 3. PlayerObject Integration

```csharp
[PlayerObject("inventory_ui")]
public class InventoryUI : UdonSharpBehaviour
{
    [PersistKey("layout")] public int layoutMode;
    [PersistKey("sort")] public int sortOrder;

    // Automatically instantiated per-player
    // Data persists across sessions
}
```

##### 4. Auto-Save System

```csharp
using UdonSharpCE.Persistence;

public class GameManager : UdonSharpBehaviour
{
    [SerializeField] private PlayerSaveData saveData;

    // Periodic auto-save (can't save in OnPlayerLeft!)
    private float _autoSaveInterval = 30f;
    private float _lastSave;

    void Update()
    {
        if (Time.time - _lastSave > _autoSaveInterval)
        {
            saveData.Save();
            _lastSave = Time.time;
        }
    }

    // Save on explicit triggers
    public void OnLevelComplete()
    {
        saveData.level++;
        saveData.Save();
    }
}
```

##### 5. Size Estimation

```csharp
// Runtime size check
int estimatedBytes = PersistenceUtils.EstimateSize(saveData);
if (estimatedBytes > 90000)  // Approaching 100KB limit
{
    Debug.LogWarning("Save data approaching size limit!");
}
```

#### Attributes Reference

| Attribute               | Description                               |
| ----------------------- | ----------------------------------------- |
| `[PlayerData("key")]`   | Maps class to PlayerData with given key   |
| `[PlayerObject("key")]` | Maps class to PlayerObject with given key |
| `[PersistKey("name")]`  | Maps field to persistence key             |
| `[Range(min, max)]`     | Validation constraint                     |
| `[MaxLength(n)]`        | String length constraint                  |

#### Lifecycle Callbacks

| Callback            | When Called                        |
| ------------------- | ---------------------------------- |
| `OnDataRestored()`  | After data loaded from persistence |
| `OnDataSaved()`     | After data successfully saved      |
| `OnDataCorrupted()` | When data fails validation/parsing |

#### VRChat Persistence Limits

| Resource                          | Limit  |
| --------------------------------- | ------ |
| PlayerData per world per player   | 100 KB |
| PlayerObject per world per player | 100 KB |

**Important:** Data cannot be saved in `OnPlayerLeft` — use periodic auto-save or save on explicit events.

---

### 4.5 CE.DevTools — Development & Debugging

**Purpose:** Comprehensive tooling for debugging and profiling VRChat worlds.

**Location:** `Runtime/Libraries/CE/DevTools` (runtime), `Editor/CE/DevTools` (editor)

#### Runtime Tools

##### 1. In-World Debug Console

```csharp
using UdonSharpCE.DevTools;

public class GameManager : UdonSharpBehaviour
{
    [SerializeField] private CEDebugConsole console;

    void Start()
    {
        console.Log("Game initialized");
        console.LogWarning("Low memory mode active");
        console.LogError("Failed to load asset");
    }

    void OnNetworkError()
    {
        console.LogError($"Network error at {Time.time:F2}s");
    }
}
```

##### 2. Performance Profiler

```csharp
using UdonSharpCE.DevTools;

public class ExpensiveSystem : UdonSharpBehaviour
{
    private CEProfiler _profiler;

    void Start()
    {
        _profiler = new CEProfiler("ExpensiveSystem");
    }

    void Update()
    {
        _profiler.BeginSample("Physics");
        DoPhysicsWork();
        _profiler.EndSample();

        _profiler.BeginSample("AI");
        DoAIWork();
        _profiler.EndSample();
    }

    public void ShowStats()
    {
        Debug.Log(_profiler.GetReport());
    }
}
```

##### 3. Performance Monitor

Drop-in component that displays:

- Frame time (ms)
- FPS (current/average/min)
- Update costs per behaviour
- Memory pressure indicators

#### Editor Tools

| Tool                | Menu Path                      | Purpose                      |
| ------------------- | ------------------------------ | ---------------------------- |
| Bandwidth Analyzer  | `CE Tools/Bandwidth Analyzer`  | Analyze sync payload sizes   |
| World Validator     | `CE Tools/World Validator`     | Pre-publish validation       |
| Network Simulator   | `CE Tools/Network Simulator`   | Simulate latency/packet loss |
| Late-Join Simulator | `CE Tools/Late-Join Simulator` | Test sync reconstruction     |
| Graph Node Browser  | `CE Tools/Graph Node Browser`  | Browse CE graph nodes        |

##### Bandwidth Analyzer

Analyzes all UdonSharp behaviours and reports:

- Sync payload sizes per behaviour
- Total bandwidth usage estimates
- Oversized payload warnings
- Optimization suggestions

##### World Validator

Pre-publish checks for common issues:

- GetComponent in Update loops
- Uninitialized synced arrays
- Invalid VRCPlayerApi usage
- Sync mode inefficiencies
- Bandwidth limit violations
- Persistence size violations

##### Network Simulator

Test your world under adverse network conditions:

- Configurable latency (0-500ms)
- Packet loss simulation (0-30%)
- Jitter simulation
- Bandwidth throttling

##### Late-Join Simulator

Test late-joiner experience:

- Capture current world state
- Simulate player join
- Verify state reconstruction
- Identify sync gaps

---

### 4.6 CE.Perf — Performance Framework

**Purpose:** Enable high-entity-count worlds through data-oriented patterns.

**Location:** `Runtime/Libraries/CE/Perf`

#### Features

##### 1. ECS-Lite Architecture

Struct-of-Arrays transformation for cache-friendly iteration:

```csharp
using UdonSharpCE.Perf;

// Define components
[CEComponent] public struct Position { public Vector3 value; }
[CEComponent] public struct Velocity { public Vector3 value; }
[CEComponent] public struct Health { public float current; public float max; }

public class BulletHellManager : UdonSharpBehaviour
{
    private CEWorld world;

    void Start()
    {
        world = new CEWorld(maxEntities: 2000);
        world.RegisterComponent<Position>();
        world.RegisterComponent<Velocity>();
        world.RegisterSystem<Position, Velocity>(MovementSystem);
    }

    [CESystem]
    private void MovementSystem(int count, Vector3[] positions, Vector3[] velocities)
    {
        float dt = Time.deltaTime;
        for (int i = 0; i < count; i++)
        {
            positions[i] += velocities[i] * dt;
        }
    }

    void Update()
    {
        world.RunSystems();
    }
}
```

##### 2. Entity Management

```csharp
// Create entities
int bulletId = world.CreateEntity();
world.SetComponent<Position>(bulletId, new Position { value = spawnPos });
world.SetComponent<Velocity>(bulletId, new Velocity { value = direction * speed });

// Query entities
CEQuery query = world.CreateQuery<Position, Velocity>();
query.ForEach(id => {
    var pos = world.GetComponent<Position>(id);
    var vel = world.GetComponent<Velocity>(id);
    // Process entity
});

// Destroy entities
world.DestroyEntityDeferred(bulletId);
```

##### 3. Object Pooling (CEPool)

```csharp
using UdonSharpCE.Perf;

public class ProjectileManager : UdonSharpBehaviour
{
    [SerializeField] private GameObject bulletPrefab;
    private CEPool<Bullet> bulletPool;

    void Start()
    {
        bulletPool = new CEPool<Bullet>(bulletPrefab, initialSize: 50);
    }

    public void Fire(Vector3 direction)
    {
        // O(1) acquire with handle
        var handle = bulletPool.AcquireHandle();
        if (handle.IsValid)
        {
            handle.Object.Fire(direction);

            // Store handle for O(1) release later
            StartCoroutine(ReturnAfterDelay(handle, 5f));
        }
    }

    private IEnumerator ReturnAfterDelay(PoolHandle<Bullet> handle, float delay)
    {
        yield return new WaitForSeconds(delay);
        bulletPool.Release(handle);  // O(1) release!
    }
}
```

##### 4. Spatial Partitioning (CEGrid)

```csharp
using UdonSharpCE.Perf;

public class EnemyManager : UdonSharpBehaviour
{
    private CEGrid<Enemy> grid;

    void Start()
    {
        // Grid with 10-unit cells, 100x100 world
        grid = new CEGrid<Enemy>(cellSize: 10f, worldSize: 100f);
    }

    public void UpdateEnemyPosition(Enemy enemy, Vector3 newPos)
    {
        grid.Move(enemy, newPos);
    }

    public Enemy[] GetNearbyEnemies(Vector3 position, float radius)
    {
        return grid.QueryRadius(position, radius);
    }
}
```

##### 5. Batch Processing

```csharp
using UdonSharpCE.Perf;

public class DamageSystem : UdonSharpBehaviour
{
    private BatchProcessor<DamageEvent> damageProcessor;

    void Start()
    {
        damageProcessor = new BatchProcessor<DamageEvent>(
            maxBatchSize: 100,
            processor: ProcessDamageBatch
        );
    }

    public void QueueDamage(int targetId, float amount)
    {
        damageProcessor.Enqueue(new DamageEvent { targetId = targetId, amount = amount });
    }

    void LateUpdate()
    {
        damageProcessor.ProcessAll();
    }

    private void ProcessDamageBatch(DamageEvent[] events, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ApplyDamage(events[i].targetId, events[i].amount);
        }
    }
}
```

#### Classes Reference

| Class               | Description                             |
| ------------------- | --------------------------------------- |
| `CEWorld`           | Entity container with archetype storage |
| `CEQuery`           | Efficient entity queries                |
| `CEPool<T>`         | Generic object pool with O(1) release   |
| `PoolHandle<T>`     | Handle for pooled objects               |
| `CEGrid<T>`         | Spatial partitioning grid               |
| `BatchProcessor<T>` | Batch processing utility                |
| `[CEComponent]`     | Marks struct as ECS component           |
| `[CESystem]`        | Marks method as ECS system              |

---

### 4.7 CE.Procgen — Procedural Generation

**Purpose:** Deterministic procedural content that generates identically across all clients.

**Location:** `Runtime/Libraries/CE/Procgen`

#### Features

##### 1. Deterministic Random (CERandom)

```csharp
using UdonSharpCE.Procgen;

public class DungeonGenerator : UdonSharpBehaviour
{
    [UdonSynced] private int worldSeed;

    public void GenerateDungeon()
    {
        // Same seed = same dungeon on all clients
        CERandom rng = new CERandom(worldSeed);

        int roomCount = rng.Range(5, 10);
        for (int i = 0; i < roomCount; i++)
        {
            Vector3 roomPos = new Vector3(
                rng.Range(-50f, 50f),
                0f,
                rng.Range(-50f, 50f)
            );
            SpawnRoom(roomPos, rng.Range(0, 4));  // Random room type
        }
    }
}
```

##### 2. Noise Functions (CENoise)

```csharp
using UdonSharpCE.Procgen;

public class TerrainGenerator : UdonSharpBehaviour
{
    public void GenerateTerrain(int seed)
    {
        CENoise noise = new CENoise(seed);

        for (int x = 0; x < 100; x++)
        {
            for (int z = 0; z < 100; z++)
            {
                // Perlin noise for height
                float height = noise.Perlin(x * 0.1f, z * 0.1f) * 10f;

                // Simplex noise for detail
                height += noise.Simplex(x * 0.5f, z * 0.5f) * 2f;

                // Worley noise for caves
                float cave = noise.Worley(x * 0.2f, z * 0.2f);

                SetTerrainHeight(x, z, height, cave < 0.3f);
            }
        }
    }
}
```

Supported noise types:

- **Perlin** — Classic smooth noise
- **Simplex** — Faster, fewer artifacts than Perlin
- **Worley** — Cellular/Voronoi patterns

##### 3. Dungeon Generation (CEDungeon)

```csharp
using UdonSharpCE.Procgen;

public class RoguelikeLevel : UdonSharpBehaviour
{
    public void GenerateLevel(int seed)
    {
        CEDungeon dungeon = new CEDungeon(seed);

        dungeon.Configure(
            minRooms: 8,
            maxRooms: 15,
            minRoomSize: 5,
            maxRoomSize: 12,
            corridorWidth: 2
        );

        DungeonData data = dungeon.Generate();

        foreach (var room in data.rooms)
        {
            SpawnRoom(room.position, room.size, room.type);
        }

        foreach (var corridor in data.corridors)
        {
            SpawnCorridor(corridor.start, corridor.end);
        }
    }
}
```

##### 4. Wave Function Collapse (WFCSolver)

Time-sliced WFC for complex pattern generation:

```csharp
using UdonSharpCE.Procgen;

public class CityGenerator : UdonSharpBehaviour
{
    private WFCSolver solver;

    public void StartGeneration(int seed)
    {
        solver = new WFCSolver(seed);

        // Define tile adjacency rules
        solver.AddTile("road_straight", /* adjacency rules */);
        solver.AddTile("road_corner", /* adjacency rules */);
        solver.AddTile("building", /* adjacency rules */);
        solver.AddTile("park", /* adjacency rules */);

        solver.Initialize(gridWidth: 20, gridHeight: 20);
    }

    void Update()
    {
        // Time-sliced: process some cells per frame
        if (!solver.IsComplete)
        {
            solver.Step(maxIterations: 10);
        }
        else if (!hasSpawned)
        {
            SpawnFromSolution(solver.GetSolution());
            hasSpawned = true;
        }
    }
}
```

#### Classes Reference

| Class       | Description                                |
| ----------- | ------------------------------------------ |
| `CERandom`  | Deterministic PRNG (Xorshift algorithm)    |
| `CENoise`   | Noise generation (Perlin, Simplex, Worley) |
| `CEDungeon` | Graph-based dungeon generator              |
| `WFCSolver` | Wave Function Collapse solver              |

---

### 4.8 CE.GraphBridge — Visual Scripting Integration

**Purpose:** Expose CE systems to Udon Graph users via attributes.

**Location:** `Runtime/Libraries/CE/GraphBridge`, `Editor/CE/GraphBridge`

#### Features

##### 1. Graph Node Attributes

```csharp
using UdonSharpCE.GraphBridge;

[GraphCategory("CE/Math")]
public class MathNodes : UdonSharpBehaviour
{
    [GraphNode("Clamp Value")]
    [GraphInput("value", "The value to clamp")]
    [GraphInput("min", "Minimum value")]
    [GraphInput("max", "Maximum value")]
    [GraphOutput("result", "The clamped value")]
    public float ClampValue(float value, float min, float max)
    {
        return Mathf.Clamp(value, min, max);
    }

    [GraphNode("Lerp Color")]
    [GraphInput("a", "Start color")]
    [GraphInput("b", "End color")]
    [GraphInput("t", "Interpolation factor (0-1)")]
    [GraphOutput("result", "Interpolated color")]
    public Color LerpColor(Color a, Color b, float t)
    {
        return Color.Lerp(a, b, t);
    }
}
```

##### 2. Properties and Events

```csharp
[GraphCategory("CE/Player")]
public class PlayerNodes : UdonSharpBehaviour
{
    [GraphProperty("Player Health")]
    public float health { get; set; }

    [GraphEvent("On Player Damaged")]
    public event Action<float> OnDamaged;

    [GraphFlowOutput("damaged")]
    public void TakeDamage(float amount)
    {
        health -= amount;
        OnDamaged?.Invoke(amount);
    }
}
```

#### Editor Tooling

##### Graph Node Browser

`CE Tools > Graph Node Browser`

- Hierarchical tree view of all graph nodes
- Search functionality
- Details panel with input/output documentation
- Category filtering

##### Code Generator

`Tools > UdonSharpCE > Generate All Wrappers`

Generates UdonSharp wrapper code for graph nodes, allowing them to be used from both Graph and C#.

##### Documentation Generator

`Tools > UdonSharpCE > Generate Node Documentation`

Auto-generates Markdown documentation for all graph nodes.

#### Attributes Reference

| Attribute                       | Description                         |
| ------------------------------- | ----------------------------------- |
| `[GraphNode("name")]`           | Marks method as graph node          |
| `[GraphInput("name", "desc")]`  | Marks parameter as graph input      |
| `[GraphOutput("name", "desc")]` | Marks return value as graph output  |
| `[GraphFlowOutput("name")]`     | Marks method as flow output trigger |
| `[GraphProperty("name")]`       | Marks property for graph access     |
| `[GraphEvent("name")]`          | Marks event for graph subscription  |
| `[GraphCategory("path")]`       | Organizes nodes in hierarchy        |

---

## 5. Runtime Optimizations

CE includes significant runtime performance improvements that benefit all users automatically.

### Library Optimizations (Shipped)

All library optimizations are complete and enabled by default.

#### 1. Hash Index Computation Fix

**Problem:** All hash-based collections used `Mathf.Abs(hashCode % capacity)` — an expensive extern call.

**Solution:** Replaced with bitwise operation `(hashCode & 0x7FFFFFFF) % capacity`.

**Impact:** 15-25% faster for all Dictionary/HashSet operations.

```csharp
// Before (slow - extern call)
int index = Mathf.Abs(hashCode % capacity);

// After (fast - bitwise operation)
int index = (hashCode & 0x7FFFFFFF) % capacity;
```

#### 2. CEDictionary Tombstone Deletion

**Problem:** Dictionary removal triggered O(n) rehash every time.

**Solution:** Implemented tombstone deletion with threshold-based rehashing.

**Impact:** O(n) → O(1) amortized for removal operations.

```csharp
// Slot states
private const byte EMPTY = 0;
private const byte OCCUPIED = 1;
private const byte TOMBSTONE = 2;

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
```

#### 3. CEWorld Pending Destruction Optimization

**Problem:** `ProcessPendingDestructions()` scanned ALL entities every tick.

**Solution:** Maintain separate pending destruction list.

**Impact:** Near-zero cost when no entities pending (common case).

```csharp
private void ProcessPendingDestructions()
{
    if (_pendingDestroyCount == 0) return;  // O(1) early exit

    for (int i = 0; i < _pendingDestroyCount; i++)
    {
        ProcessDestruction(_pendingDestroyList[i]);
    }
    _pendingDestroyCount = 0;
}
```

#### 4. CEQuery Hot Path Optimization

**Problem:** Query iteration made 3 method calls per entity.

**Solution:** Direct array access for hot loops.

**Impact:** 30-60% faster query iteration.

```csharp
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

#### 5. CEPool O(1) Release

**Problem:** `Release(T obj)` performed O(n) linear search.

**Solution:** Handle-based API returns index with object.

**Impact:** O(n) → O(1) for release operations.

```csharp
// Handle struct for O(1) release
public struct PoolHandle<T>
{
    public readonly int Index;
    public readonly T Object;
    public bool IsValid => Index >= 0;
}

// Usage
var handle = bulletPool.AcquireHandle();
Bullet bullet = handle.Object;
// ... use bullet ...
bulletPool.Release(handle);  // O(1) release!
```

#### 6. Collection Unchecked Access

**Problem:** Every indexed access performs bounds checking.

**Solution:** Added unchecked access methods for hot loops.

**Impact:** 10-20% faster for tight loops.

```csharp
// Available on List<T>, CEList<T>, Queue<T>, Stack<T>
T GetUnchecked(int index);          // No bounds check
void SetUnchecked(int index, T v);  // No bounds check
T[] GetBackingArray();              // Direct array access
```

#### 7. Allocation-Free Iteration

**Problem:** Every `foreach` loop allocates an iterator object.

**Solution:** Added `ForEach` methods and cached iterators.

**Impact:** Zero allocations per iteration.

```csharp
// No allocation (unlike foreach)
enemies.ForEach(e => e.TakeDamage(10));

// With index
enemies.ForEachWithIndex((e, i) => e.SetIndex(i));

// With early exit
enemies.ForEachUntil(e => {
    if (e.IsDead) return false;
    e.Update();
    return true;
});
```

### Performance Impact Summary

| Operation                          | Before      | After       | Improvement |
| ---------------------------------- | ----------- | ----------- | ----------- |
| Dictionary.Add                     | 1.0x        | 1.2x        | 20%         |
| Dictionary.TryGetValue             | 1.0x        | 1.25x       | 25%         |
| Dictionary.Remove                  | 1.0x (O(n)) | 2-5x (O(1)) | 100-400%    |
| HashSet.Contains                   | 1.0x        | 1.2x        | 20%         |
| CEQuery.ForEach                    | 1.0x        | 1.5x        | 50%         |
| CEWorld.ProcessPending (0 pending) | 1.0x        | 100x+       | 10000%+     |
| CEPool.Release                     | 1.0x (O(n)) | 10x+ (O(1)) | 1000%+      |
| List foreach                       | 1.0x        | 1.3x        | 30%         |

---

## 6. Compile-Time Analyzers

CE includes Roslyn-based analyzers that detect common issues at compile time.

### Analyzer Reference

| Analyzer                         | Category      | Detects                                                  |
| -------------------------------- | ------------- | -------------------------------------------------------- |
| `GetComponentAnalyzer`           | Performance   | `GetComponent` calls in Update/FixedUpdate/LateUpdate    |
| `UninitializedSyncArrayAnalyzer` | Networking    | Uninitialized `[UdonSynced]` arrays                      |
| `SyncPayloadAnalyzer`            | Networking    | Oversized continuous sync payloads                       |
| `NamedArgumentAnalyzer`          | Compatibility | Named arguments (unsupported in U#)                      |
| `LocalOnlyNetworkCallAnalyzer`   | Networking    | `SendCustomNetworkEvent` targeting `[LocalOnly]` methods |
| `PlayerApiAfterLeaveAnalyzer`    | Safety        | Invalid VRCPlayerApi usage in OnPlayerLeft               |
| `SyncModeAnalyzer`               | Performance   | Inefficient continuous sync usage                        |
| `PersistenceSizeAnalyzer`        | Persistence   | `[PlayerData]` schema exceeding size limits              |

### Example Warnings

#### GetComponent in Update

```csharp
void Update()
{
    var renderer = GetComponent<Renderer>();  // ⚠️ CE001: Cache GetComponent result
    renderer.material.color = Color.red;
}
```

#### Uninitialized Synced Array

```csharp
[UdonSynced] public int[] scores;  // ⚠️ CE002: Initialize synced array
```

**Fix:**

```csharp
[UdonSynced] public int[] scores = new int[16];
```

#### Oversized Sync Payload

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class TooBig : UdonSharpBehaviour
{
    [UdonSynced] public float[] data = new float[100];  // ⚠️ CE003: Exceeds 200-byte limit
}
```

#### Named Arguments

```csharp
DoSomething(target: player, delay: 1.0f);  // ⚠️ CE004: Named arguments not supported
```

**Fix:**

```csharp
DoSomething(player, 1.0f);
```

---

## 7. Editor Tools

### Tool Overview

| Tool                | Menu Path                        | Purpose                     |
| ------------------- | -------------------------------- | --------------------------- |
| Bandwidth Analyzer  | `CE Tools > Bandwidth Analyzer`  | Analyze sync payload sizes  |
| World Validator     | `CE Tools > World Validator`     | Pre-publish validation      |
| Network Simulator   | `CE Tools > Network Simulator`   | Simulate network conditions |
| Late-Join Simulator | `CE Tools > Late-Join Simulator` | Test late-join sync         |
| Graph Node Browser  | `CE Tools > Graph Node Browser`  | Browse graph nodes          |

### Bandwidth Analyzer

Analyzes all synced behaviours in your world:

**Features:**

- Per-behaviour sync payload size estimation
- Total bandwidth usage calculation
- Sync mode recommendations
- Export to CSV for analysis

**Output Example:**

```
═══════════════════════════════════════════════════════════
Bandwidth Analysis Report
═══════════════════════════════════════════════════════════

PlayerController (Continuous)
  ├─ position: Vector3 (12 bytes)
  ├─ rotation: Quaternion (16 bytes)
  └─ health: float (4 bytes)
  Total: 32 bytes
  Status: ✓ Within 200-byte continuous limit

ScoreBoard (Manual)
  ├─ scores: int[16] (64 bytes)
  ├─ teamNames: string[4] (~80 bytes est.)
  └─ gameTime: float (4 bytes)
  Total: ~148 bytes
  Status: ✓ Within 11KB/s manual budget

World Total Continuous: 32 bytes/tick
World Total Manual: ~1.2 KB/sync
```

### World Validator

Pre-publish validation checklist:

**Validators:**

| Validator                | Category    | Description                          |
| ------------------------ | ----------- | ------------------------------------ |
| GetComponentInUpdate     | Performance | Flags uncached GetComponent          |
| UninitializedSyncedArray | Networking  | Flags null synced arrays             |
| PlayerApiAfterLeave      | Safety      | Flags invalid player API usage       |
| LocalOnlyNetworkCall     | Networking  | Flags network calls to local methods |
| SyncModeValidator        | Performance | Recommends sync mode changes         |
| BandwidthValidator       | Networking  | Checks bandwidth limits              |
| PersistenceSizeValidator | Persistence | Checks PlayerData size limits        |

**Output Example:**

```
═══════════════════════════════════════════════════════════
World Validation Report
═══════════════════════════════════════════════════════════

✓ No GetComponent calls in Update loops
✓ All synced arrays initialized
⚠ Warning: PlayerInventory.cs line 45
    VRCPlayerApi used after OnPlayerLeft may be invalid
✗ Error: NetworkManager.cs line 123
    SendCustomNetworkEvent targets [LocalOnly] method
✓ Sync modes optimally configured
✓ Bandwidth within limits
✓ Persistence data within size limits

Summary: 1 Error, 1 Warning, 5 Passed
```

### Network Simulator

Test your world under adverse network conditions:

**Configurable Parameters:**

- **Latency:** 0-500ms (simulates round-trip delay)
- **Packet Loss:** 0-30% (simulates dropped packets)
- **Jitter:** 0-100ms (simulates latency variance)
- **Bandwidth Limit:** Throttle to specific KB/s

**Usage:**

1. Open `CE Tools > Network Simulator`
2. Configure desired conditions
3. Enter Play Mode
4. Test networking features
5. Review console for issues

### Late-Join Simulator

Test what late-joiners experience:

**Features:**

- Capture current world state
- Simulate player disconnect/rejoin
- Verify state reconstruction
- Identify missing sync data
- Generate sync coverage report

**Usage:**

1. Open `CE Tools > Late-Join Simulator`
2. Play your world to desired state
3. Click "Capture State"
4. Click "Simulate Late Join"
5. Review reconstruction results

---

## 8. Inspector System

CE provides an enhanced inspector experience for UdonSharp behaviours.

### Features

#### 1. Single Component View

The raw UdonBehaviour component is hidden when a proxy exists, showing only one clean component.

#### 2. CE Branding

- **CE Badge:** Indicates CE is managing the behaviour
- **CE ✓ Badge:** Indicates optimizations are applied
- **Additional Badges:** Netcode, Pooled, Predicted

#### 3. Status Bar

Quick overview of sync status:

```
Sync: Continuous │ 6 synced vars │ Optimized: -45% bandwidth
```

#### 4. Property Grouping

Automatic grouping of related fields:

- **References:** UnityEngine.Object fields
- **Configuration:** Numeric primitives
- **Synced Variables:** `[UdonSynced]` fields
- **Debug Options:** Fields containing "debug"
- **Internal:** Underscore-prefixed fields

#### 5. Optimization Panel

Shows applied CE optimizations:

```
CE Optimizations
  ✓ Sync Packing: 6 → 2 variables
  ✓ Delta Sync: position, rotation
  ✓ Constants Folded: 4 expressions
```

### Configuration

Access via `Preferences > UdonSharp CE > Inspector`:

| Setting                       | Description                              | Default   |
| ----------------------------- | ---------------------------------------- | --------- |
| Hide UdonBehaviour Components | Hide raw UdonBehaviour when proxy exists | ✓ Enabled |
| Show Optimization Info        | Display CE optimization details          | ✓ Enabled |
| Auto-Group Properties         | Automatically group related properties   | ✓ Enabled |

### Menu Items

| Menu                                                                 | Action                   |
| -------------------------------------------------------------------- | ------------------------ |
| `Tools > UdonSharp CE > Inspector > Toggle UdonBehaviour Visibility` | Show/hide raw components |
| `Tools > UdonSharp CE > Inspector > Refresh All Inspectors`          | Force refresh            |
| `Tools > UdonSharp CE > Inspector > Open Preferences`                | Open settings            |

---

## 9. VPM Distribution

### The Problem We Solve

Merlin's 1.2-beta1 release notes warned:

> "Installation is also super jank due to how VRC has a copy of U# directly in the SDK. I want to make it better, but **I advise against using this in prefabs you are looking to distribute**."

This existed because:

1. Manual installation required deleting SDK files
2. No way for prefabs to declare dependency on 1.2-beta1
3. VCC unaware of installation, risked silent overwrites
4. No version tracking or auto-updates

### Our Solution: VPM Community Repository

UdonSharpCE distributes via a VPM community repository.

**Repository URL:** `https://charlvs.github.io/vpm/index.json`

### Installation

1. Open VRChat Creator Companion
2. Settings → Packages → Add Repository
3. Enter repository URL
4. Open project → Install UdonSharpCE
5. VCC handles conflict resolution automatically

### Package Conflict Handling

The core package declares explicit conflict with official UdonSharp:

```json
{
  "name": "com.charlvs.udonsharpce",
  "displayName": "UdonSharp Community Edition",
  "version": "1.0.0",
  "conflicts": {
    "com.vrchat.udonsharp": "*"
  },
  "provides": {
    "com.vrchat.udonsharp": "1.2.0"
  },
  "vpmDependencies": {
    "com.vrchat.worlds": ">=3.5.0"
  }
}
```

**What this achieves:**

- `conflicts`: VCC knows it cannot install both simultaneously
- `provides`: VCC treats UdonSharpCE as satisfying `com.vrchat.udonsharp` dependencies
- Existing prefabs that depend on UdonSharp work without modification
- **Drop-in replacement** with zero migration for end users

### Prefab Distribution

With VPM, prefab creators can safely depend on UdonSharpCE features:

```json
{
  "name": "com.coolcreator.awesome-inventory",
  "vpmDependencies": {
    "com.charlvs.udonsharpce": ">=1.0.0",
    "com.charlvs.udonsharpce.data": ">=1.0.0",
    "com.charlvs.udonsharpce.persist": ">=1.0.0"
  }
}
```

When users install the prefab, VCC automatically:

1. Detects dependency on UdonSharpCE
2. Removes official UdonSharp (conflict)
3. Installs UdonSharpCE and required modules
4. Everything works — no manual steps

---

## 10. Known Limitations & Pitfalls

### Language Limitations

| Limitation                            | CE Approach                          |
| ------------------------------------- | ------------------------------------ |
| Named arguments not supported         | Never use in CE APIs; use positional |
| Complex optional parameters           | Prefer overloads or builder patterns |
| Static fields not truly static        | Emulate internally, don't expose     |
| No struct support                     | SoA transformation in CE.Perf        |
| Collection initializers not supported | Use explicit `.Add()` calls          |
| Enum casting quirks                   | Wrap in helper methods               |

#### Named Arguments

```csharp
// ❌ WILL NOT COMPILE
DoSomething(target: player, delay: 1.0f);

// ✅ Use positional arguments
DoSomething(player, 1.0f);
```

#### Optional Parameters

```csharp
// ❌ Can cause issues
public void Method(Vector3 pos = default, string name = nameof(Method)) { }

// ✅ Use overloads instead
public void Method() => Method(Vector3.zero, "Method");
public void Method(Vector3 pos) => Method(pos, "Method");
public void Method(Vector3 pos, string name) { /* ... */ }
```

#### Static Fields

```csharp
// This works but each UdonBehaviour type gets its own "static"
public class MyBehaviour : UdonSharpBehaviour
{
    private static int counter = 0;  // Not shared across types!
}
```

#### Object Initializers vs Collection Initializers

```csharp
// ✅ Object initializers ARE supported
var task = new UdonTask { _status = TaskStatus.RanToCompletion };
var config = new MyConfig { Width = 100, Height = 200 };

// ❌ Collection initializers are NOT supported
var list = new List<int> { 1, 2, 3 };  // Will not compile

// ✅ Use explicit Add() calls instead
var list = new List<int>();
list.Add(1);
list.Add(2);
list.Add(3);
```

### Performance Constraints

| Constraint                     | CE Approach                                  |
| ------------------------------ | -------------------------------------------- |
| 200-1000x slower than C#       | Batch operations, avoid per-entity overhead  |
| GetComponent is slow           | Cache all references in Start()              |
| Cross-behaviour calls slow     | Minimize, use events where possible          |
| 11 KB/s network budget         | Delta encoding, quantization, prioritization |
| 200 byte continuous sync limit | Prefer manual sync for complex state         |

#### GetComponent in Hot Paths

```csharp
// ❌ SLOW - triggers analyzer warning
void Update()
{
    var renderer = GetComponent<Renderer>();
    renderer.material.color = Color.red;
}

// ✅ Cache in Start
private Renderer _renderer;
void Start() { _renderer = GetComponent<Renderer>(); }
void Update() { _renderer.material.color = Color.red; }
```

#### Cross-Behaviour Calls

```csharp
// ❌ SLOW in tight loops
for (int i = 0; i < 1000; i++)
{
    otherBehaviour.DoSomething(i);
}

// ✅ Batch or use events
otherBehaviour.ProcessBatch(dataArray);
```

### Networking Constraints

| Constraint              | Limit          |
| ----------------------- | -------------- |
| Continuous sync payload | 200 bytes max  |
| Manual sync bandwidth   | 11 KB/s budget |

#### Uninitialized Synced Arrays

```csharp
// ❌ WILL BREAK SYNC
[UdonSynced] public int[] scores;  // null = sync fails silently

// ✅ Initialize arrays
[UdonSynced] public int[] scores = new int[16];
```

#### String Sync in Continuous Mode

```csharp
// ❌ Strings in continuous mode very limited
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class Chat : UdonSharpBehaviour
{
    [UdonSynced] public string message;  // Limited to ~50 chars
}

// ✅ Use Manual sync for strings
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class Chat : UdonSharpBehaviour
{
    [UdonSynced] public string message;
}
```

### Persistence Constraints

| Constraint                        | Limit  |
| --------------------------------- | ------ |
| PlayerData per world per player   | 100 KB |
| PlayerObject per world per player | 100 KB |

#### Cannot Save in OnPlayerLeft

```csharp
// ❌ TOO LATE - data won't save
public override void OnPlayerLeft(VRCPlayerApi player)
{
    if (player.isLocal)
        SavePlayerData();  // Will not persist!
}

// ✅ Save periodically or on explicit triggers
public void OnGameEvent() { SavePlayerData(); }
private void AutoSave() { /* Called on timer */ }
```

### Nested Prefab Limitations

**Warning:** UdonSharp has always warned against nested prefabs, and in 1.x+ they can completely break.

**Symptom:**

```
Cannot upgrade scene behaviour 'SomethingOrOther' since its prefab must be upgraded
```

**CE Guidance:**

- Avoid nested prefabs containing UdonSharpBehaviours
- If upgrading from 0.x: unpack nested prefabs first
- CE prefabs should be flat (no nested U# prefabs)
- Use "UdonSharp → Force Upgrade" menu if encountering issues

---

## 11. Migration Guide

### From Official SDK UdonSharp

#### Step 1: Install UdonSharpCE via VPM

1. Add repository: `https://charlvs.github.io/vpm/index.json`
2. Install UdonSharpCE (VCC handles conflict resolution)

#### Step 2: Verify Compilation

All existing scripts should compile without changes. The base API is identical.

#### Step 3: Adopt CE Features (Optional)

Gradually adopt CE features as needed:

```csharp
// Add using statements for CE namespaces
using UdonSharpCE.Data;
using UdonSharpCE.Async;
using UdonSharpCE.Net;
// etc.
```

### From Merlin 1.2-beta1 Manual Install

#### Step 1: Remove Manual Installation

1. Delete `Packages/com.merlin.UdonSharp` folder (if exists)
2. Restore official UdonSharp from SDK (VCC will help)

#### Step 2: Install UdonSharpCE via VPM

Follow VPM installation steps above.

#### Step 3: Verify Compilation

All 1.2-beta1 features are included. Scripts should compile unchanged.

### API Compatibility

CE maintains full backward compatibility:

| Pattern                      | Works?                 |
| ---------------------------- | ---------------------- |
| Old `foreach` loops          | ✅ (with allocation)   |
| Old `list[i]` access         | ✅ (with bounds check) |
| Old `pool.Release(obj)`      | ✅ (O(n) search)       |
| Old `[UdonSynced]`           | ✅                     |
| Old `SendCustomNetworkEvent` | ✅                     |

New CE APIs are purely additive — no changes required to existing code.

### Recommended Migration Path

1. **Week 1:** Install CE, verify everything compiles
2. **Week 2:** Run World Validator, fix any warnings
3. **Week 3:** Adopt performance optimizations (unchecked access, ForEach)
4. **Month 2+:** Gradually adopt CE modules as needed

---

## 12. API Quick Reference

### CE.Data

```csharp
// Collections
CEList<T> list = new CEList<T>();
CEDictionary<K, V> dict = new CEDictionary<K, V>();

// Bridges
DataList dataList = list.AsDataList();
CEList<T> fromData = CEList<T>.FromDataList(dataList);

// Attributes
[DataModel] class MyModel { }
[DataField("key")] public int field;
```

### CE.Async

```csharp
// Task types
public async UdonTask DoWork() { }
public async UdonTask<int> GetValue() { }

// Primitives
await UdonTask.Delay(1.0f);
await UdonTask.Yield();
await UdonTask.WhenAll(task1, task2);
await UdonTask.WhenAny(task1, task2);

// Cancellation
CancellationToken token;
token.Cancel();
if (token.IsCancellationRequested) return;
```

### CE.Net

```csharp
// Attributes
[Sync] public int value;
[Sync(DeltaEncode = true)] public int[] array;
[Rpc(Target = RpcTarget.All)] public void Method() { }
[RpcOwnerOnly] public void OwnerMethod() { }
[LocalOnly] private void LocalMethod() { }
[SyncOnJoin] public class MyBehaviour { }

// Rate limiting
RateLimiter limiter = new RateLimiter(5f);
if (limiter.CanSend()) { /* ... */ }
```

### CE.Persistence

```csharp
// Attributes
[PlayerData("key")] public class MyData { }
[PlayerObject("key")] public class MyObject { }
[PersistKey("field_key")] public int field;
[Range(0f, 100f)] public float value;
[MaxLength(32)] public string text;

// Callbacks
void OnDataRestored() { }
void OnDataSaved() { }
void OnDataCorrupted() { }

// Utilities
int size = PersistenceUtils.EstimateSize(data);
```

### CE.DevTools

```csharp
// Debug console
console.Log("message");
console.LogWarning("warning");
console.LogError("error");

// Profiler
CEProfiler profiler = new CEProfiler("name");
profiler.BeginSample("section");
profiler.EndSample();
string report = profiler.GetReport();
```

### CE.Perf

```csharp
// ECS
CEWorld world = new CEWorld(maxEntities);
world.RegisterComponent<MyComponent>();
int entity = world.CreateEntity();
world.SetComponent<MyComponent>(entity, data);
world.DestroyEntityDeferred(entity);

// Queries
CEQuery query = world.CreateQuery<Component1, Component2>();
query.ForEach(id => { });
int count = query.Count();

// Pool
CEPool<T> pool = new CEPool<T>(prefab, size);
PoolHandle<T> handle = pool.AcquireHandle();
T obj = handle.Object;
pool.Release(handle);

// Collections
T item = list.GetUnchecked(i);
list.SetUnchecked(i, value);
T[] array = list.GetBackingArray();
list.ForEach(item => { });
list.ForEachWithIndex((item, i) => { });
list.ForEachUntil(item => condition);
```

### CE.Procgen

```csharp
// Random
CERandom rng = new CERandom(seed);
int value = rng.Range(min, max);
float fvalue = rng.Range(minF, maxF);

// Noise
CENoise noise = new CENoise(seed);
float perlin = noise.Perlin(x, y);
float simplex = noise.Simplex(x, y);
float worley = noise.Worley(x, y);

// Dungeon
CEDungeon dungeon = new CEDungeon(seed);
dungeon.Configure(minRooms, maxRooms, minSize, maxSize, corridorWidth);
DungeonData data = dungeon.Generate();

// WFC
WFCSolver solver = new WFCSolver(seed);
solver.AddTile(name, adjacencyRules);
solver.Initialize(width, height);
solver.Step(maxIterations);
TileData[] solution = solver.GetSolution();
```

### CE.GraphBridge

```csharp
// Attributes
[GraphCategory("Category/Subcategory")]
[GraphNode("Node Name")]
[GraphInput("param", "description")]
[GraphOutput("result", "description")]
[GraphFlowOutput("output_name")]
[GraphProperty("Property Name")]
[GraphEvent("Event Name")]
```

---

## Appendix: World Types Enabled by CE

UdonSharpCE enables world types that are currently impractical or impossible with standard tools:

| World Type            | CE Features Used                                                   |
| --------------------- | ------------------------------------------------------------------ |
| **Roguelike RPGs**    | CE.Procgen (dungeons), CE.Persistence (saves), CE.Data (inventory) |
| **RTS Games**         | CE.Perf (ECS for units), CE.Net (sync), CE.Grid (spatial queries)  |
| **Story Experiences** | CE.Async (cutscenes), CE.Net (sync state)                          |
| **Creation Tools**    | CE.Data (models), CE.Persistence (saves), CE.GraphBridge           |
| **Competitive Games** | CE.Net (typed networking), CE.DevTools (debugging)                 |
| **Persistent Worlds** | CE.Persistence (economy), CE.Data (complex state)                  |

---

_UdonSharp Community Edition — Enabling the Next Generation of VRChat Experiences_

_Document Version 1.0 — December 2025_
