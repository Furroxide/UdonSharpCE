# UdonSharpCE — Unified Enhancement Proposal

_A modular framework for building ambitious VRChat worlds_

---

## Executive Summary

UdonSharpCE (Community Edition) builds on MerlinVR's UdonSharp 1.2-beta1 to provide a cohesive, modular framework that makes ambitious VRChat worlds practical to build and maintain. Rather than chasing full C# parity, UdonSharpCE focuses on **raising the abstraction level** where it matters most: data management, asynchronous workflows, networking, persistence, performance optimization, and procedural content.

This proposal defines eight modules with clear boundaries, explicit non-goals, and design constraints informed by real Udon/VRChat limitations.

### Progress Audit (Dec 2024)

- Phase 1 (DevTools core, Data): Implemented in `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/DevTools` and `/CE/Data`.
- Phase 2 (Persistence + analyzers): Attribute/validation/runtime size estimator and analyzers implemented; PlayerObject helpers, lifecycle callbacks, and compile-time size warnings still pending.
- Phase 3 (Async + Net core): UdonTask runtime, Sync/Rpc/LocalOnly attributes, and analyzers implemented; async state-machine emitter and fuller bandwidth estimator still pending.
- Phase 4 (Perf): ECS-lite, pooling, grid/LOD utilities implemented in `/CE/Perf`.
- Phase 5 (Procgen, Net advanced, GraphBridge): Not started.

---

## Baseline: UdonSharp 1.2-beta1

UdonSharpCE treats **Merlin's 1.2-beta1** as the minimum baseline. This provides:

- ✅ Non-UdonSharpBehaviour class support
- ✅ Generic types
- ✅ Built-in collections: `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`
- ✅ Operator overloading
- ✅ Custom type serialization (persistence-compatible)

**Known limitations we accept:**

- ❌ No inheritance on non-behaviour types
- ❌ No native struct support (we work around this)
- ❌ No true static fields (we emulate internally)

---

## Module Architecture

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

---

## CE.Data — Ergonomic Data Layer

### Goal

Type-safe, ergonomic data abstractions bridging Merlin's collections to VRChat's Data Containers (`DataList`, `DataDictionary`, `DataToken`).

**Status (Dec 2024):** Implemented (`Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Data`, samples in `Packages/com.merlin.UdonSharp/Samples~/CE/DataModels`).

### Features

1. **Collection Bridges**

   - Seamless conversion between `List<T>` ↔ `DataList`
   - `Dictionary<K,V>` ↔ `DataDictionary` mapping
   - Type-safe `DataToken` wrappers

2. **Model Definitions**

   - Attribute-based field mapping
   - Validation helpers
   - Default value handling

3. **Serialization Utilities**
   - JSON-like serialization for complex types
   - Schema versioning support

### Example

```csharp
using UdonSharpCE.Data;

[DataModel]
public class InventoryItem
{
    [DataField("id")] public int itemId;
    [DataField("qty")] public int quantity;
    [DataField("meta")] public string metadata;
}

public class InventoryManager : UdonSharpBehaviour
{
    private CEList<InventoryItem> items = new CEList<InventoryItem>();

    public void AddItem(int id, int qty)
    {
        items.Add(new InventoryItem { itemId = id, quantity = qty });
    }

    public DataList ToDataList() => items.AsDataList();
}
```

### Upstream Alignment

- Uses Merlin's generics & collections as the foundation
- Does not reinvent collection types
- Focuses on bridging and ergonomics, not reimplementation

---

## CE.Async — Coroutine & Task System

### Goal

Provide async/await-style workflows compiled into Udon-compatible state machines for complex sequences, cutscenes, quests, and multi-step logic.

**Status (Dec 2024):** Partial. Runtime `UdonTask`/`UdonTask<T>` APIs and analyzers are in-place (`Runtime/Libraries/CE/Async`, `Editor/CE/Async/AsyncMethodAnalyzer.cs`); the state-machine emitter is still pending.

### Features

1. **UdonTask\<T\>**

   - Lightweight promise-like structure
   - Compiled to array-based state storage
   - Supports cancellation tokens

2. **Async Method Transformation**

   - `await` keyword compiles to state machine
   - Frame-delayed continuations via `SendCustomEventDelayedFrames`
   - Automatic local variable preservation

3. **Coordination Primitives**

   - `UdonTask.WhenAll()` — wait for multiple tasks
   - `UdonTask.WhenAny()` — wait for first completion
   - `UdonTask.Delay()` — time-based waiting
   - `UdonTask.Yield()` — yield to next frame

4. **Sequence Builder API**
   - Fluent API for non-async sequential logic
   - Visual-friendly for simpler use cases

### Example

```csharp
using UdonSharpCE.Async;

public class CutsceneController : UdonSharpBehaviour
{
    [SerializeField] private Animator cameraAnimator;
    [SerializeField] private DialogueUI dialogue;

    public async UdonTask PlayIntro()
    {
        await FadeScreen.ToBlack(1.0f);
        await dialogue.ShowText("Welcome, traveler...", 3.0f);
        await cameraAnimator.PlayAsync("PanToVillage");
        await FadeScreen.FromBlack(1.0f);

        // Run two things in parallel
        await UdonTask.WhenAll(
            SpawnVillagers(),
            StartAmbientAudio()
        );
    }

    public async UdonTask SpawnVillagers() { /* ... */ }
    public async UdonTask StartAmbientAudio() { /* ... */ }
}
```

### Worlds This Enables

- Story-driven experiences with scripted cinematics
- Multi-step puzzle sequences
- Turn-based multiplayer games
- Quest systems with branching logic
- Progressive world loading

### Upstream Alignment

- Method export attributes (`[EventExport]`) enable internal steps to be event-addressable
- Fake statics (internal) power the global task scheduler

---

## CE.Net — Typed Networking Layer

### Goal

Type-safe RPC and sync with clear guarantees, visibility control, and compile-time analysis for network budget and safety.

**Status (Dec 2024):** Partial. Core attributes (`[Sync]`, `[Rpc]`, `[LocalOnly]`), rate limiter, and analyzers are live (`Runtime/Libraries/CE/Net`, `Editor/CE/Net`). Advanced late-join sync and conflict helpers are not yet implemented.

### Features

1. **Visibility Attributes**

   ```csharp
   [LocalOnly]      // Cannot be called via SendCustomNetworkEvent
   [Rpc]            // Explicitly exported as network RPC
   [RpcOwnerOnly]   // RPC only callable by owner
   [EventExport]    // Private but exported for SendCustomEvent
   ```

2. **Typed Sync Properties**

   ```csharp
   [Sync]                           // Basic sync
   [Sync(Interpolation.Linear)]     // Interpolated
   [Sync(DeltaEncode = true)]       // Delta compression
   [Sync(Quantize = 0.01f)]         // Float quantization
   ```

3. **Compile-Time Analysis**

   - Bandwidth estimation per behaviour
   - Warnings for oversized sync payloads
   - Detection of common sync bugs (uninitialized arrays, etc.)

4. **Late-Joiner Sync**

   - Automatic state reconstruction from sync properties
   - `[SyncOnJoin]` attribute for explicit late-join handling

5. **RPC Parameter Marshalling**
   - Type-safe parameters up to 8 arguments
   - Automatic validation
   - Rate limiting support

### Example

```csharp
using UdonSharpCE.Net;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ScoreBoard : UdonSharpBehaviour
{
    [Sync] public int redScore;
    [Sync] public int blueScore;
    [Sync(DeltaEncode = true)] public int[] playerScores = new int[16];

    [Rpc(Target = RpcTarget.All, RateLimit = 5)]
    public void AnnounceGoal(int team, int scorerId)
    {
        PlayGoalAnimation(team);
        UpdateScoreUI();
    }

    [LocalOnly]  // Cannot be called over network
    private void PlayGoalAnimation(int team) { /* ... */ }

    [RpcOwnerOnly]  // Only owner can call
    public void ResetScores()
    {
        redScore = 0;
        blueScore = 0;
        RequestSerialization();
    }
}
```

### Worlds This Enables

- Complex multiplayer games with reliable state sync
- Collaborative building/editing worlds
- Competitive games with authoritative state
- Social worlds with shared persistent state

### Upstream Alignment

- Incorporates `[LocalOnly]` / `[NonNetworked]` concept from Issue #112, #143
- Addresses array sync quirks noted in community issues
- Avoids patterns known to be problematic (continuous sync for critical state)

---

## CE.Persistence — ORM-Style Data Mapping

### Goal

Attribute-based mapping from C# models to VRChat's PlayerData and PlayerObject systems, making persistent worlds dramatically easier to build.

**Status (Dec 2024):** Partial. Attribute mapping, validation helpers, runtime persistence API, and size estimator are implemented (`Runtime/Libraries/CE/Persistence`, samples in `Samples~/CE/Persistence`). PlayerObject helpers, lifecycle callbacks, and compile-time size warnings remain TODO.

### Features

1. **PlayerData Mapping**

   ```csharp
   [PlayerData("inventory")]
   public class PlayerInventory
   {
       [PersistKey("gold")] public int gold;
       [PersistKey("items")] public int[] itemIds;
       [PersistKey("slots")] public int[] itemCounts;
   }
   ```

2. **PlayerObject Integration**

   - Automatic PlayerObject instantiation handling
   - Synced variable persistence via `VRCEnablePersistence`
   - Migration helpers for schema changes

3. **Lifecycle Events**

   ```csharp
   void OnDataRestored(RestoreResult result);
   void OnDataSaved(SaveResult result);
   void OnDataCorrupted(CorruptionInfo info);
   ```

4. **Validation & Constraints**

   ```csharp
   [PersistKey("level"), Range(1, 100)] public int level;
   [PersistKey("name"), MaxLength(32)] public string displayName;
   ```

5. **Quota Management**
   - Compile-time size estimation (100KB limit awareness)
   - Warnings when approaching limits
   - Compression hints

### Example

```csharp
using UdonSharpCE.Persistence;

[PlayerData("rpg_save")]
public class PlayerSaveData
{
    [PersistKey("xp")] public int experience;
    [PersistKey("lvl")] public int level;
    [PersistKey("pos")] public Vector3 lastPosition;
    [PersistKey("inv")] public int[] inventory = new int[50];
    [PersistKey("flags")] public byte[] questFlags = new byte[32];
}

public class SaveManager : UdonSharpBehaviour
{
    private PlayerSaveData saveData;

    public void OnDataRestored(RestoreResult result)
    {
        if (result == RestoreResult.Success)
        {
            TeleportPlayer(saveData.lastPosition);
            LoadInventoryUI(saveData.inventory);
        }
        else if (result == RestoreResult.NoData)
        {
            // New player - initialize defaults
            saveData = PlayerSaveData.CreateDefault();
        }
    }

    public void SaveGame()
    {
        saveData.lastPosition = localPlayer.GetPosition();
        CEPersistence.Save(saveData);
    }
}
```

### Worlds This Enables

- RPGs with persistent character progression
- Collectible/achievement systems
- Customizable personal spaces
- Incremental/idle games with offline progress
- Social worlds with persistent reputation/currency

### Upstream Alignment

- Bridges Merlin's serialization to VRChat's persistence
- Uses generics for type-safe data containers
- Accounts for 100KB PlayerData / 100KB PlayerObject limits

---

## CE.GraphBridge — Visual Scripting Integration

### Goal

Expose UdonSharpCE systems to Udon Graph users via attributes, turning CE into a platform non-coders can adopt.

**Status (Dec 2024):** Not started. No GraphBridge runtime/editor code is present yet.

### Features

1. **Node Export Attributes**

   ```csharp
   [GraphNode("Inventory/Add Item")]
   [GraphInput("Item ID", typeof(int))]
   [GraphInput("Quantity", typeof(int))]
   [GraphOutput("Success", typeof(bool))]
   public bool AddItem(int itemId, int quantity) { /* ... */ }
   ```

2. **Property Exposure**

   ```csharp
   [GraphProperty("Player/Gold", ReadOnly = false)]
   public int Gold { get; set; }
   ```

3. **Event Nodes**

   ```csharp
   [GraphEvent("Inventory/On Item Added")]
   public void OnItemAdded(int itemId) { /* ... */ }
   ```

4. **Custom Node Definitions**
   - Fluent API for complex node definitions
   - Dropdown/enum support
   - Validation hints in Graph UI

### Example

```csharp
using UdonSharpCE.GraphBridge;

public class QuestSystem : UdonSharpBehaviour
{
    [GraphNode("Quests/Start Quest")]
    [GraphInput("Quest ID", typeof(string))]
    [GraphOutput("Already Active", typeof(bool))]
    public bool StartQuest(string questId)
    {
        if (IsQuestActive(questId)) return true;
        ActivateQuest(questId);
        return false;
    }

    [GraphNode("Quests/Complete Objective")]
    [GraphInput("Quest ID", typeof(string))]
    [GraphInput("Objective Index", typeof(int))]
    public void CompleteObjective(string questId, int index)
    {
        MarkObjectiveComplete(questId, index);
        CheckQuestCompletion(questId);
    }

    [GraphEvent("Quests/On Quest Completed")]
    public void OnQuestCompleted(string questId) { }
}
```

### Worlds This Enables

- Prefab ecosystems usable by non-programmers
- Team collaboration (artists/designers + programmers)
- Educational experiences
- Rapid prototyping with visual iteration

### Upstream Alignment

- Uses method export logic for node generation
- Follows upstream naming/symbol conventions
- Avoids collisions with built-in Udon nodes

---

## CE.DevTools — Development & Debugging

### Goal

Make ambitious Udon worlds testable, debuggable, and maintainable through comprehensive tooling.

**Status (Dec 2024):** Core console/profiler/logger and analyzers are implemented (`Runtime/Libraries/CE/DevTools`, `Editor/CE/Analyzers`). Network/state inspector UX is still conceptual.

### Features

1. **In-World Debug Console**

   - Floating UI for logs, errors, warnings
   - Filter by severity and source
   - Collapsible stack traces

2. **Performance Profiler**

   - Per-behaviour execution timing
   - Frame time breakdown
   - Memory usage estimation

3. **Network Inspector**

   - Visualize sync traffic in real-time
   - Ownership map
   - Bandwidth usage per behaviour

4. **State Inspector**

   - Live variable watching
   - Edit values at runtime (debug builds)
   - Breakpoint-like state snapshots

5. **Compile-Time Analysis**

   - Constant folding optimization (Issue #107)
   - Network budget warnings
   - Common anti-pattern detection:
     - `GetComponent` in Update loops
     - Uninitialized synced arrays
     - Named argument usage (unsupported)
     - Complex optional parameter chains

6. **Log Integration**

   - RuntimeLogWatcher file parsing (upstream PR)
   - Paste external logs for analysis
   - Symbol mapping to source lines

7. **Conditional Compilation**
   - `[DebugOnly]` attribute for debug-only code
   - Automatic stripping for release builds
   - Upload-safe instrumentation

### Example

```csharp
using UdonSharpCE.DevTools;

public class GameManager : UdonSharpBehaviour
{
    [DebugOnly]
    private void Update()
    {
        CEDebug.DrawNetworkOverlay(this);
        CEDebug.LogFrameTime("GameManager.Update");
    }

    public void OnPlayerJoined(VRCPlayerApi player)
    {
        CEDebug.Log($"Player joined: {player.displayName}", LogLevel.Info);

        // This will trigger a compile-time warning:
        // ⚠️ GetComponent in frequently-called method
        var renderer = GetComponent<Renderer>();
    }
}

// Compile-time analyzer output:
// ⚠️ Line 15: GetComponent<Renderer>() called in OnPlayerJoined.
//    Consider caching in Start().
// ⚠️ Behaviour 'ScoreSync' estimates 245 bytes sync payload.
//    Continuous sync limit is 200 bytes.
```

### Upstream Alignment

- Inherits RuntimeLogWatcher from upstream
- Implements constant folding (Issue #107)
- Warns about patterns from community issue tracker

---

## CE.Perf — Performance Framework

### Goal

Enable high-entity-count worlds through data-oriented patterns that work within Udon's performance constraints.

**Status (Dec 2024):** Implemented. CEWorld, CEGrid, CEPool, LOD helpers, and ECS utilities live in `Runtime/Libraries/CE/Perf`.

### Features

1. **ECS-Lite Architecture**

   - Array-of-Structs (AoS) compiled to Structure-of-Arrays (SoA)
   - Compile-time query generation
   - Batched update loops

2. **Struct-to-SoA Transformation**

   ```csharp
   // User writes:
   [CEComponent]
   public struct Enemy {
       public Vector3 position;
       public float health;
       public int state;
   }

   // Compiler generates:
   // Vector3[] enemy_position;
   // float[] enemy_health;
   // int[] enemy_state;
   ```

3. **System Definitions**

   ```csharp
   [CESystem]
   public void MovementSystem(ref Position pos, ref Velocity vel)
   {
       pos.value += vel.value * Time.deltaTime;
   }
   ```

4. **Object Pooling**

   - Pre-allocated pools with warm-up
   - Type-safe acquire/release
   - Automatic recycling

5. **Spatial Partitioning**
   - Grid-based broad phase
   - Query helpers for nearby entities
   - LOD-aware update frequencies

### Example

```csharp
using UdonSharpCE.Perf;

[CEComponent] public struct Position { public Vector3 value; }
[CEComponent] public struct Velocity { public Vector3 value; }
[CEComponent] public struct Projectile { public float damage; public float lifetime; }

public class BulletHellManager : UdonSharpBehaviour
{
    private CEWorld world;

    void Start()
    {
        world = new CEWorld(maxEntities: 2000);
        world.RegisterSystem<Position, Velocity>(MovementSystem);
        world.RegisterSystem<Projectile>(LifetimeSystem);
    }

    void Update()
    {
        world.Tick();  // Runs all systems in optimized batches
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

    public void SpawnBullet(Vector3 pos, Vector3 vel, float damage)
    {
        int entity = world.CreateEntity();
        world.Set(entity, new Position { value = pos });
        world.Set(entity, new Velocity { value = vel });
        world.Set(entity, new Projectile { damage = damage, lifetime = 5f });
    }
}
```

### Worlds This Enables

- Bullet-hell games (1000+ projectiles)
- RTS games (hundreds of units)
- Ecosystem simulations (flocking, predator-prey)
- Particle effects beyond Unity's Udon limitations
- Physics puzzles with many interacting objects

### Why This Matters

Udon runs 200-1000x slower than native C#. Traditional approaches hit walls at 50-100 entities. ECS-Lite enables 10-20x more entities by:

- Eliminating per-entity method call overhead
- Enabling cache-friendly memory access patterns
- Batching operations into single Udon calls

---

## CE.Procgen — Procedural Generation

### Goal

Enable deterministic procedural content that generates identically across all clients from shared seeds.

**Status (Dec 2024):** Not started. No CE.Procgen runtime/editor code exists yet.

### Features

1. **Deterministic Random**

   ```csharp
   var rng = new CERandom(seed: 12345);
   float value = rng.NextFloat();      // Same on all clients
   int index = rng.Range(0, 10);       // Same on all clients
   Vector3 point = rng.InsideUnitSphere();
   ```

2. **Noise Functions**

   - Perlin noise (1D, 2D, 3D)
   - Simplex noise
   - Worley/cellular noise
   - Fractal/octave combinations

3. **Wave Function Collapse**

   - Constraint-based tile placement
   - Backtracking solver
   - Time-sliced execution (no frame drops)

4. **Graph-Based Generation**

   - Room/corridor dungeon generation
   - Connectivity guarantees
   - Critical path analysis

5. **Chunk/Tile Pooling**
   - Pre-instantiated tile pools
   - Runtime repositioning and reconfiguration
   - LOD-aware activation

### Example

```csharp
using UdonSharpCE.Procgen;

public class DungeonGenerator : UdonSharpBehaviour
{
    [SerializeField] private GameObject[] roomPrefabs;
    [SerializeField] private int poolSize = 50;

    private CERandom rng;
    private RoomPool roomPool;

    public void GenerateDungeon(int seed)
    {
        // All players with same seed get identical dungeon
        rng = new CERandom(seed);

        var layout = CEDungeon.Generate(
            rng: rng,
            roomCount: 15,
            minSize: new Vector2Int(3, 3),
            maxSize: new Vector2Int(8, 8),
            connectivity: 0.3f
        );

        // Reposition pooled rooms to match layout
        for (int i = 0; i < layout.rooms.Length; i++)
        {
            var room = roomPool.Get();
            room.transform.position = layout.rooms[i].worldPosition;
            room.Configure(layout.rooms[i].type, rng);
        }
    }

    public void GenerateTerrain(int seed)
    {
        rng = new CERandom(seed);

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                float height = CENoise.Fractal2D(
                    x * 0.1f, z * 0.1f,
                    octaves: 4,
                    persistence: 0.5f
                );
                heightmap[x, z] = height * maxHeight;
            }
        }
    }
}
```

### Worlds This Enables

- Roguelike dungeons with infinite layouts
- Procedural terrain exploration
- Puzzle games with random challenges
- Card games with deterministic shuffling
- Survival games with resource distribution

---

## Design Constraints

CE APIs are designed to avoid fighting known Udon/VRChat limitations:

### Language Limitations to Avoid

| Limitation                      | CE Approach                          |
| ------------------------------- | ------------------------------------ |
| Named arguments not supported   | Never use in CE APIs                 |
| Complex optional parameters     | Prefer overloads or builder patterns |
| Static fields not truly static  | Emulate internally, don't expose     |
| Struct overhead in Udon         | SoA transformation in CE.Perf        |
| Enum casting quirks             | Wrap in helper methods               |
| Array sync with continuous mode | Prefer manual sync for arrays        |

### Performance Constraints

| Constraint                     | CE Approach                                  |
| ------------------------------ | -------------------------------------------- |
| 200-1000x slower than C#       | Batch operations, avoid per-entity overhead  |
| GetComponent is slow           | Cache all references in Start()              |
| Cross-behaviour calls are slow | Minimize, use events where possible          |
| 11 KB/s network budget         | Delta encoding, quantization, prioritization |
| 200 byte continuous sync limit | Prefer manual sync for complex state         |

### Persistence Constraints

| Constraint                 | CE Approach                            |
| -------------------------- | -------------------------------------- |
| 100KB PlayerData limit     | Runtime size estimator; compile-time warnings planned |
| 100KB PlayerObject limit   | Schema design guidance                 |
| No save slots built-in     | CE.Persistence provides abstraction    |
| Can't save in OnPlayerLeft | Auto-save system with periodic flush   |

---

## Explicit Non-Goals

UdonSharpCE will **NOT** pursue:

### Language Features

- ❌ `goto` or unstructured control flow
- ❌ Full C# language specification compliance
- ❌ Features encouraging naive per-frame iteration over large datasets
- ❌ Deep inheritance hierarchies (prefer composition + generics)
- ❌ Reflection or dynamic type resolution

### Overselling Internal Workarounds

- ❌ Marketing "fake statics" as full static support
- ❌ Claiming struct support when we're doing SoA transformation
- ❌ Pretending Udon performance limitations don't exist

### Scope Creep

- ❌ Avatar scripting (out of Udon's scope)
- ❌ Client mods or security bypasses
- ❌ Anything requiring VRChat client modifications

---

## Implementation Phases

Each phase builds on the previous. A phase is complete when its modules are functional and tested.

Status legend: `[x]` done, `[~]` in progress/partial, `[ ]` not started.

```
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 1: Foundation                                             │
│ ┌─────────────┐  ┌─────────────┐                                │
│ │ CE.DevTools │  │  CE.Data    │                                │
│ │   (core)    │  │             │                                │
│ └─────────────┘  └─────────────┘                                │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 2: Persistence                                            │
│ ┌─────────────────┐  ┌─────────────────┐                        │
│ │ CE.Persistence  │  │   CE.DevTools   │                        │
│ │                 │  │  (analyzers)    │                        │
│ └─────────────────┘  └─────────────────┘                        │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 3: Workflows                                              │
│ ┌─────────────┐  ┌─────────────┐                                │
│ │  CE.Async   │  │   CE.Net    │                                │
│ │             │  │   (core)    │                                │
│ └─────────────┘  └─────────────┘                                │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 4: Performance                                            │
│ ┌─────────────────────────────┐                                 │
│ │          CE.Perf            │                                 │
│ │  (ECS-Lite, pooling, SoA)   │                                 │
│ └─────────────────────────────┘                                 │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 5: Content & Access                                       │
│ ┌─────────────┐  ┌─────────────┐  ┌─────────────┐               │
│ │ CE.Procgen  │  │   CE.Net    │  │CE.GraphBridge│              │
│ │             │  │ (advanced)  │  │             │               │
│ └─────────────┘  └─────────────┘  └─────────────┘               │
└─────────────────────────────────────────────────────────────────┘
```

---

### Phase 1: Foundation

**Modules:** CE.DevTools (core), CE.Data

**Why first:**

- CE.DevTools enables debugging and validation for all subsequent work
- CE.Data provides the collection/model primitives everything else depends on

**Deliverables:**

- [x] In-world debug console (log viewer, error display)
- [x] Basic profiler (frame timing, Update costs)
- [x] `CEList<T>`, `CEDictionary<K,V>` wrappers
- [x] `DataList` / `DataDictionary` bridge methods
- [x] `[DataModel]` and `[DataField]` attribute handling

**Exit criteria:** Can create data models, bridge to Data Containers, and debug basic behaviours in-world.

---

### Phase 2: Persistence

**Modules:** CE.Persistence, CE.DevTools (analyzers)

**Depends on:** Phase 1 (CE.Data for models, CE.DevTools for debugging)

**Why second:**

- Persistence unlocks the highest-impact world category (progression, saves, customization)
- Analyzers catch common mistakes before they become runtime bugs

**Deliverables:**

- [x] `[PlayerData]` and `[PersistKey]` attribute mapping
- [ ] PlayerObject integration helpers
- [ ] `OnDataRestored` / `OnDataSaved` lifecycle events
- [~] Compile-time size estimation (runtime estimator in CEPersistence; compiler warnings pending)
- [x] Analyzer: uninitialized synced arrays
- [x] Analyzer: `GetComponent` in Update/FixedUpdate
- [x] Analyzer: oversized sync payloads

**Exit criteria:** Can define persistent player data with attributes, save/restore works correctly, compile-time warnings catch common issues.

---

### Phase 3: Workflows

**Modules:** CE.Async, CE.Net (core)

**Depends on:** Phase 2 (persistence patterns inform async save flows; analyzers validate network code)

**Why third:**

- Async enables complex game logic (quests, cutscenes, sequences)
- Net (core) enables basic multiplayer with type safety

**Deliverables:**

- [x] `UdonTask` / `UdonTask<T>` promise types
- [ ] State machine compiler transformation for `async`/`await`
- [x] `UdonTask.Delay()`, `UdonTask.Yield()`, `UdonTask.WhenAll()`
- [x] `[Sync]` attribute with interpolation/quantization options
- [x] `[Rpc]` attribute with target and rate limiting
- [x] `[LocalOnly]` attribute (non-networked methods)
- [~] Bandwidth estimation per behaviour (continuous sync analyzer only)

**Exit criteria:** Can write async sequences that compile to valid Udon; can define typed RPCs and sync properties with compile-time validation.

---

### Phase 4: Performance

**Modules:** CE.Perf

**Depends on:** Phase 3 (async patterns used for time-sliced operations; net patterns for synced entity state)

**Why fourth:**

- Performance framework enables high-entity-count worlds
- Requires stable foundation before optimizing

**Deliverables:**

- [x] `[CEComponent]` struct definitions
- [x] Struct-to-SoA compiler transformation
- [x] `CEWorld` entity container with archetype storage
- [x] `[CESystem]` attribute and system registration
- [x] Batched update loop execution
- [x] `CEPool<T>` object pooling
- [x] Spatial partitioning (grid-based)

**Exit criteria:** Can define ECS-style components and systems; demo showing 500+ entities updating without frame drops.

---

### Phase 5: Content & Access

**Modules:** CE.Procgen, CE.Net (advanced), CE.GraphBridge

**Depends on:** Phase 4 (procgen benefits from ECS for generated entities; graph bridge exposes all prior modules)

**Why last:**

- These extend reach rather than enable core functionality
- GraphBridge should expose a complete, stable API

**Deliverables:**

- [ ] `CERandom` deterministic PRNG
- [ ] `CENoise` (Perlin, Simplex, Worley)
- [ ] `CEDungeon` graph-based room generation
- [ ] Wave Function Collapse solver (time-sliced)
- [ ] CE.Net: late-joiner state reconstruction
- [ ] CE.Net: `[SyncOnJoin]` attribute
- [ ] CE.Net: conflict resolution helpers
- [ ] `[GraphNode]`, `[GraphInput]`, `[GraphOutput]` attributes
- [ ] `[GraphProperty]`, `[GraphEvent]` attributes
- [ ] Editor tooling for node generation

**Exit criteria:** Can generate deterministic procedural content synced across clients; Udon Graph users can use CE systems via generated nodes.

---

### Phase Dependencies Summary

| Phase | Modules                              | Hard Dependencies | Soft Dependencies  |
| ----- | ------------------------------------ | ----------------- | ------------------ |
| 1     | DevTools (core), Data                | —                 | —                  |
| 2     | Persistence, DevTools (analyzers)    | Phase 1           | —                  |
| 3     | Async, Net (core)                    | Phase 2           | —                  |
| 4     | Perf                                 | Phase 3           | —                  |
| 5     | Procgen, Net (advanced), GraphBridge | Phase 4           | All modules stable |

**Hard dependency:** Cannot start phase N+1 until phase N is complete.

**Soft dependency:** Benefits from prior work but could technically start earlier with stubs.

---

## Upstream Maintenance

### Staying Current

- Periodic rebase on `vrchat-community/UdonSharp` for platform fixes
- Cherry-pick relevant fixes (enum bugs, log parsing, etc.)
- Track VRChat SDK releases for new Udon capabilities

### Contribution Back

- Bug fixes applicable to upstream should be PR'd back
- CE-specific features stay in CE
- Documentation improvements shared with community

---

## Conclusion

UdonSharpCE's eight modules create a cohesive framework that:

1. **Raises abstraction** where it matters (data, async, networking, persistence)
2. **Pushes boundaries** where others accept limits (performance, procedural content)
3. **Expands access** to powerful tools (graph bridge, dev tools)
4. **Stays grounded** in real constraints (explicit non-goals, design rules)

The result enables world types that are currently impractical or impossible:

- **Roguelike RPGs** with procedural dungeons, inventory, and persistent progression
- **RTS games** with hundreds of units and complex AI
- **Story-driven experiences** with cinematic sequences and branching narratives
- **Collaborative creation tools** rivaling standalone applications
- **Competitive multiplayer games** with proper networking architecture
- **Persistent social worlds** with economy, reputation, and customization

**This is not incremental improvement—this is the foundation for the next generation of VRChat experiences.**

---

_UdonSharpCE Enhancement Proposal — December 2024_  
_Synthesized from original proposal and upstream alignment analysis_
