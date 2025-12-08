# UdonSharpCE — Unified Enhancement Proposal

_A modular framework for building ambitious VRChat worlds_

**Version:** 2.1 (December 2025)  
**Status:** All phases complete — ready for stabilization and release

---

## Executive Summary

UdonSharpCE (Community Edition) builds on MerlinVR's UdonSharp 1.2-beta1 to provide a cohesive, modular framework that makes ambitious VRChat worlds practical to build and maintain. Rather than chasing full C# parity, UdonSharpCE focuses on **raising the abstraction level** where it matters most: data management, asynchronous workflows, networking, persistence, performance optimization, and procedural content.

This proposal defines eight modules with clear boundaries, explicit non-goals, and design constraints informed by real Udon/VRChat limitations.

### Progress Summary (Dec 2025)

| Phase   | Modules                                  | Status                                                           |
| ------- | ---------------------------------------- | ---------------------------------------------------------------- |
| Phase 1 | CE.DevTools (core), CE.Data              | ✅ Complete                                                      |
| Phase 2 | CE.Persistence, Analyzers                | ✅ Complete (runtime + compile-time analyzers shipped)           |
| Phase 3 | CE.Async, CE.Net (core)                  | ✅ Complete (state-machine transformer + networking polish pass) |
| Phase 4 | CE.Perf                                  | ✅ Complete                                                      |
| Phase 5 | CE.Procgen, CE.Net (adv), CE.GraphBridge | ✅ Complete (all editor tooling implemented)                     |

**Key milestones achieved:**

- VPM distribution infrastructure designed and ready for deployment
- Bandwidth Analyzer and World Validator editor tools implemented
- Async state-machine transformer and CE.Net late-join flow implemented
- Procgen runtime suite (random, noise, dungeon, WFC) landed
- Network Simulator with latency/packet loss simulation implemented
- Late-Join Simulator for testing sync reconstruction implemented
- Graph Node Browser, Code Generator, and Documentation Generator implemented

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

## VPM Distribution Strategy

### The Problem We Solve

Merlin's 1.2-beta1 release notes explicitly warned:

> "Installation is also super jank due to how VRC has a copy of U# directly in the SDK. I want to make it better, but **I advise against using this in prefabs you are looking to distribute**."

This warning existed because:

1. Manual installation required deleting SDK files
2. No way for prefabs to declare dependency on 1.2-beta1
3. VCC unaware of installation, risked silent overwrites
4. No version tracking or auto-updates

### Our Solution: VPM Community Repository

UdonSharpCE distributes via a VPM community repository hosted on GitHub Pages.

**Repository URL:** `https://charlvs.github.io/vpm/index.json`

**User Installation:**

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

### Prefab Distribution Now Works

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

### Repository Structure

```
UdonSharpCE/
├── docs/                              ← GitHub Pages root
│   ├── index.json                     ← VPM repository listing
│   ├── index.html                     ← Landing page
│   └── packages/
│       └── com.charlvs.udonsharpce/
│           ├── 1.0.0.zip
│           └── ...
├── Packages/
│   ├── com.charlvs.udonsharpce/       ← Source packages
│   │   ├── package.json
│   │   ├── Editor/
│   │   ├── Runtime/
│   │   └── Samples~/
│   └── ...
└── .github/workflows/
    └── release.yml                    ← Automated release
```

### Automated Release Pipeline

GitHub Actions workflow triggers on version tags:

```yaml
on:
  push:
    tags: ["v*"]

jobs:
  release:
    steps:
      - name: Build packages
        run: ./scripts/release_all.sh ${{ github.ref_name }}
      - name: Update VPM listing
        run: python ./scripts/update_vpm_listing.py
      - name: Deploy to GitHub Pages
        uses: peaceiris/actions-gh-pages@v3
```

**Release process:**

```bash
git tag v1.0.0
git push origin v1.0.0
# GitHub Actions handles the rest
```

---

## Implementation Requirements & Pitfalls

This section documents critical requirements and common pitfalls discovered during implementation. **Read this before writing any module code.**

### Critical: Assembly Definition Requirements

Every UdonSharp script must belong to a U# assembly definition.

```
Assets/
├── YourProject/
│   ├── YourProject.asmdef           ← Standard assembly
│   ├── YourProject.UdonSharp.asmdef ← Required U# assembly
│   └── Scripts/
│       └── MyBehaviour.cs
```

**Symptom if missing:**

```
[UdonSharp] Script 'Assets/MyScript.cs' does not belong to a U# assembly,
have you made a U# assembly definition for the assembly the script is a part of?
```

**CE Implementation:** All CE packages include proper `.asmdef` files with U# companions.

### Critical: Nested Prefab Limitations

**⚠️ UdonSharp has always warned against nested prefabs, and in 1.x+ they can completely break.**

**Symptom:**

```
Cannot upgrade scene behaviour 'SomethingOrOther' since its prefab must be upgraded
```

**CE Guidance:**

- Avoid nested prefabs containing UdonSharpBehaviours
- If upgrading from 0.x: unpack nested prefabs first
- CE prefabs should be flat (no nested U# prefabs)
- Use "UdonSharp → Force Upgrade" menu if encountering issues

### Critical: Serialization Differences (1.x vs 0.x)

In UdonSharp 1.x, **data is owned by the C# proxy**, not the UdonBehaviour:

```csharp
// Old mental model (0.x): UdonBehaviour holds data at runtime
// New model (1.x): C# proxy holds data, UdonBehaviour is empty until runtime
```

**Implications for CE:**

- Editor scripts must use the C# proxy, not UdonBehaviour
- Custom editors need `UdonSharpEditorUtility` for proper access
- Serialization callbacks behave differently

### Pitfall: Named Arguments Not Supported

```csharp
// ❌ WILL NOT COMPILE
DoSomething(target: player, delay: 1.0f);

// ✅ Use positional arguments
DoSomething(player, 1.0f);
```

**CE Policy:** No CE API uses named arguments. All parameters are positional or use overloads.

### Pitfall: Optional Parameters with Complex Defaults

```csharp
// ❌ Can cause issues
public void Method(Vector3 pos = default, string name = nameof(Method)) { }

// ✅ Use overloads instead
public void Method() => Method(Vector3.zero, "Method");
public void Method(Vector3 pos) => Method(pos, "Method");
public void Method(Vector3 pos, string name) { /* ... */ }
```

**CE Policy:** Prefer explicit overloads over optional parameters for public APIs.

### Pitfall: Static Fields Are Not Truly Static

Udon doesn't support true static fields. UdonSharp emulates them per-behaviour-type.

```csharp
// This works but each UdonBehaviour type gets its own "static"
public class MyBehaviour : UdonSharpBehaviour
{
    private static int counter = 0;  // Not shared across types!
}
```

**CE Approach:** Internal fake-static pattern for schedulers/managers. Never expose as true static to users.

### Pitfall: GetComponent in Hot Paths

```csharp
// ❌ SLOW - triggers analyzer warning
void Update()
{
    var renderer = GetComponent<Renderer>();  // Called every frame!
    renderer.material.color = Color.red;
}

// ✅ Cache in Start
private Renderer _renderer;
void Start() { _renderer = GetComponent<Renderer>(); }
void Update() { _renderer.material.color = Color.red; }
```

**CE DevTools Analyzer:** Warns on GetComponent in Update/FixedUpdate/LateUpdate.

### Pitfall: Uninitialized Synced Arrays

```csharp
// ❌ WILL BREAK SYNC - arrays must be initialized
[UdonSynced] public int[] scores;  // null = sync fails silently

// ✅ Initialize arrays
[UdonSynced] public int[] scores = new int[16];
```

**CE DevTools Analyzer:** Warns on uninitialized `[UdonSynced]` arrays.

### Pitfall: Continuous Sync Limits

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class TooBig : UdonSharpBehaviour
{
    [UdonSynced] public float[] bigArray = new float[100];  // ~400 bytes!
}
```

**VRChat limits:**

- Continuous sync: **200 bytes max**
- Manual sync: **11 KB/s bandwidth budget**

**CE DevTools Analyzer:** Estimates payload size, warns when exceeding limits.

### Pitfall: Cross-Behaviour Calls Are Slow

```csharp
// ❌ SLOW in tight loops
for (int i = 0; i < 1000; i++)
{
    otherBehaviour.DoSomething(i);  // Udon call overhead each iteration
}

// ✅ Batch or use events
otherBehaviour.ProcessBatch(dataArray);
```

**CE.Perf addresses this:** ECS-Lite batches operations to minimize cross-behaviour calls.

### Pitfall: String Sync in Continuous Mode

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class Chat : UdonSharpBehaviour
{
    [UdonSynced] public string message;  // Limited to ~50 chars effectively
}
```

**CE Guidance:** Use Manual sync for strings, or keep them very short in Continuous mode.

### Requirement: Persistence Size Limits

VRChat persistence limits:

- **PlayerData:** 100 KB per world per player
- **PlayerObject:** 100 KB per world per player
- Data is compressed; actual limit may be higher if compressible

**CE.Persistence:** Includes runtime size estimator, compile-time warnings planned.

### Requirement: Can't Save in OnPlayerLeft

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

**CE.Persistence:** Provides auto-save system with periodic flush.

### Testing Requirements

Before release, each CE module must pass:

1. **Compilation Test:** All scripts compile without errors
2. **Editor Test:** No errors on entering play mode in editor
3. **Build Test:** World builds and uploads successfully
4. **Client Test:** Features work in VRChat client (local)
5. **Network Test:** Features work in multiplayer (2+ clients)
6. **Late-Joiner Test:** State syncs correctly for players joining mid-session

**Recommended:** Use VRChat's "Number of Clients" = 2 for local network testing.

---

## Module Specifications

### CE.Data — Ergonomic Data Layer

**Goal:** Type-safe, ergonomic data abstractions bridging Merlin's collections to VRChat's Data Containers.

**Status:** ✅ Complete  
**Location:** `Packages/com.charlvs.udonsharpce/Runtime/Libraries/CE/Data`

#### Features

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

#### Example

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

---

### CE.Async — Coroutine & Task System

**Goal:** Async/await-style workflows compiled into Udon-compatible state machines.

**Status:** ✅ Core implementation done (state machine transformer + analyzers in place)

- ✅ Runtime `UdonTask`/`UdonTask<T>` APIs
- ✅ Analyzers
- ✅ State-machine emitter

**Location:** `Runtime/Libraries/CE/Async`, `Editor/CE/Async/AsyncMethodAnalyzer.cs`

#### Features

1. **UdonTask\<T\>** — Lightweight promise-like structure
2. **Async Method Transformation** — `await` compiles to state machine
3. **Coordination Primitives** — `WhenAll`, `WhenAny`, `Delay`, `Yield`
4. **Sequence Builder API** — Fluent API for simpler use cases

#### Example

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

---

### CE.Net — Typed Networking Layer

**Goal:** Type-safe RPC and sync with compile-time analysis.

**Status:** ✅ Core + late-join workflow implemented (tuning pending)

- ✅ Core attributes (`[Sync]`, `[Rpc]`, `[LocalOnly]`)
- ✅ Rate limiter
- ✅ Analyzers
- ✅ Late-join sync + `[SyncOnJoin]` helpers
- ✅ Conflict resolution helpers

**Location:** `Runtime/Libraries/CE/Net`, `Editor/CE/Net`

#### Features

1. **Visibility Attributes** — `[LocalOnly]`, `[Rpc]`, `[RpcOwnerOnly]`, `[EventExport]`
2. **Typed Sync Properties** — Interpolation, delta encoding, quantization
3. **Compile-Time Analysis** — Bandwidth estimation, oversized payload warnings
4. **RPC Parameter Marshalling** — Type-safe up to 8 arguments

#### Example

```csharp
using UdonSharpCE.Net;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ScoreBoard : UdonSharpBehaviour
{
    [Sync] public int redScore;
    [Sync(DeltaEncode = true)] public int[] playerScores = new int[16];

    [Rpc(Target = RpcTarget.All, RateLimit = 5)]
    public void AnnounceGoal(int team, int scorerId) { /* ... */ }

    [LocalOnly]
    private void PlayGoalAnimation(int team) { /* ... */ }
}
```

---

### CE.Persistence — ORM-Style Data Mapping

**Goal:** Attribute-based mapping to VRChat's PlayerData and PlayerObject systems.

**Status:** ✅ Feature-complete (callbacks + PlayerObject + analyzers shipped)

- ✅ Attribute mapping (`[PlayerData]`, `[PersistKey]`)
- ✅ Validation helpers
- ✅ Runtime size estimator
- ✅ PlayerObject helpers
- ✅ Lifecycle callbacks
- ✅ Compile-time size warnings

**Location:** `Runtime/Libraries/CE/Persistence`, `Samples~/CE/Persistence`

#### Features

1. **PlayerData Mapping** — Attribute-based field mapping
2. **PlayerObject Integration** — Auto-instantiation handling
3. **Lifecycle Events** — `OnDataRestored`, `OnDataSaved`, `OnDataCorrupted`
4. **Validation & Constraints** — `[Range]`, `[MaxLength]`
5. **Quota Management** — Size estimation, limit warnings

#### Example

```csharp
using UdonSharpCE.Persistence;

[PlayerData("rpg_save")]
public class PlayerSaveData
{
    [PersistKey("xp")] public int experience;
    [PersistKey("lvl")] public int level;
    [PersistKey("inv")] public int[] inventory = new int[50];
}
```

---

### CE.DevTools — Development & Debugging

**Goal:** Comprehensive tooling for debugging and profiling.

**Status:** ✅ Complete (all tools shipped)

- ✅ In-world debug console
- ✅ Performance profiler
- ✅ Compile-time analyzers
- ✅ Bandwidth Analyzer (Editor Window)
- ✅ World Validator (Editor Window)
- ✅ Network Simulator (Editor Window)
- ✅ Late-Join Simulator (Editor Window)

**Location:** `Runtime/Libraries/CE/DevTools`, `Editor/CE/Analyzers`, `Editor/CE/DevTools`

#### Editor Tools Implemented

| Tool                | Purpose                                        | Menu                           |
| ------------------- | ---------------------------------------------- | ------------------------------ |
| Bandwidth Analyzer  | Analyze sync payload sizes and bandwidth usage | `CE Tools/Bandwidth Analyzer`  |
| World Validator     | Pre-publish validation for common issues       | `CE Tools/World Validator`     |
| Network Simulator   | Simulate latency, packet loss, jitter          | `CE Tools/Network Simulator`   |
| Late-Join Simulator | Test late-joiner sync reconstruction           | `CE Tools/Late-Join Simulator` |

#### Compile-Time Analyzers

| Analyzer                         | Detects                                       |
| -------------------------------- | --------------------------------------------- |
| `GetComponentAnalyzer`           | GetComponent in Update/FixedUpdate/LateUpdate |
| `UninitializedSyncArrayAnalyzer` | Uninitialized `[UdonSynced]` arrays           |
| `SyncPayloadAnalyzer`            | Oversized continuous sync payloads            |
| `NamedArgumentAnalyzer`          | Named arguments (unsupported)                 |

#### Runtime Validators (World Validator)

| Validator                | Category    | Detects                                                |
| ------------------------ | ----------- | ------------------------------------------------------ |
| GetComponentInUpdate     | Performance | GetComponent calls in Update loops                     |
| UninitializedSyncedArray | Networking  | Uninitialized `[UdonSynced]` arrays                    |
| PlayerApiAfterLeave      | Safety      | Invalid VRCPlayerApi usage in OnPlayerLeft             |
| LocalOnlyNetworkCall     | Networking  | SendCustomNetworkEvent targeting `[LocalOnly]` methods |
| SyncModeValidator        | Performance | Inefficient continuous sync usage                      |
| BandwidthValidator       | Networking  | Bandwidth limit violations                             |
| PersistenceSizeValidator | Persistence | `[PlayerData]` schema size limit violations            |

---

### CE.Perf — Performance Framework

**Goal:** Enable high-entity-count worlds through data-oriented patterns.

**Status:** ✅ Complete  
**Location:** `Runtime/Libraries/CE/Perf`

#### Features

1. **ECS-Lite Architecture** — SoA transformation, compile-time queries
2. **`[CEComponent]`** — Struct definitions compiled to parallel arrays
3. **`[CESystem]`** — Batched update loops
4. **`CEPool<T>`** — Object pooling
5. **`CEGrid`** — Spatial partitioning

#### Example

```csharp
using UdonSharpCE.Perf;

[CEComponent] public struct Position { public Vector3 value; }
[CEComponent] public struct Velocity { public Vector3 value; }

public class BulletHellManager : UdonSharpBehaviour
{
    private CEWorld world;

    void Start()
    {
        world = new CEWorld(maxEntities: 2000);
        world.RegisterSystem<Position, Velocity>(MovementSystem);
    }

    [CESystem]
    private void MovementSystem(int count, Vector3[] positions, Vector3[] velocities)
    {
        float dt = Time.deltaTime;
        for (int i = 0; i < count; i++)
            positions[i] += velocities[i] * dt;
    }
}
```

---

### CE.Procgen — Procedural Generation

**Goal:** Deterministic procedural content that generates identically across all clients.

**Status:** 🟡 Runtime implemented (validation, samples, and tuning pending)

#### Implemented Features

1. **CERandom** — Deterministic PRNG (Xorshift)
2. **CENoise** — Perlin, Simplex, Worley noise
3. **CEDungeon** — Graph-based room generation
4. **WFC Solver** — Wave Function Collapse (time-sliced)

---

### CE.GraphBridge — Visual Scripting Integration

**Goal:** Expose CE systems to Udon Graph users via attributes.

**Status:** ✅ Complete (attributes + editor tooling shipped)

#### Current Capabilities

1. **`[GraphNode]` / `[GraphInput]` / `[GraphOutput]` / `[GraphFlowOutput]`** — Attribute set for exposing methods and ports
2. **`[GraphProperty]` / `[GraphEvent]` / `[GraphCategory]`** — Attribute set for properties, events, and grouping
3. **Editor Tooling** — Full suite implemented:
   - Graph Node Browser (`CE Tools/Graph Node Browser`) — Browse, search, and inspect all graph nodes
   - Code Generator (`Tools/UdonSharpCE/Generate All Wrappers`) — Generate UdonSharp wrapper code
   - Documentation Generator (`Tools/UdonSharpCE/Generate Node Documentation`) — Auto-generate Markdown docs

---

## Design Constraints

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
| 100KB PlayerData limit     | Compile-time size estimation, warnings |
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

## Implementation Roadmap

### Phase Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 1: Foundation                                    ✅ DONE  │
│ ┌─────────────┐  ┌─────────────┐                                │
│ │ CE.DevTools │  │   CE.Data   │                                │
│ │   (core)    │  │             │                                │
│ └─────────────┘  └─────────────┘                                │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 2: Persistence                                   ✅ DONE  │
│ ┌─────────────────┐  ┌─────────────────┐                        │
│ │ CE.Persistence  │  │   CE.DevTools   │                        │
│ │                 │  │  (analyzers)    │                        │
│ └─────────────────┘  └─────────────────┘                        │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 3: Workflows                                     ✅ DONE  │
│ ┌─────────────┐  ┌─────────────┐                                │
│ │  CE.Async   │  │   CE.Net    │                                │
│ │             │  │   (core)    │                                │
│ └─────────────┘  └─────────────┘                                │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 4: Performance                                   ✅ DONE  │
│ ┌─────────────────────────────┐                                 │
│ │          CE.Perf            │                                 │
│ │  (ECS-Lite, pooling, SoA)   │                                 │
│ └─────────────────────────────┘                                 │
└──────────────────────┬──────────────────────────────────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 5: Content & Access                              ✅ DONE  │
│ ┌─────────────┐  ┌─────────────┐  ┌─────────────┐               │
│ │ CE.Procgen  │  │   CE.Net    │  │CE.GraphBridge│              │
│ │             │  │ (advanced)  │  │ + Tooling   │               │
│ └─────────────┘  └─────────────┘  └─────────────┘               │
└─────────────────────────────────────────────────────────────────┘
```

### Detailed Phase Checklists

#### Phase 1: Foundation ✅

- [x] In-world debug console (log viewer, error display)
- [x] Basic profiler (frame timing, Update costs)
- [x] `CEList<T>`, `CEDictionary<K,V>` wrappers
- [x] `DataList` / `DataDictionary` bridge methods
- [x] `[DataModel]` and `[DataField]` attribute handling

#### Phase 2: Persistence ✅

- [x] `[PlayerData]` and `[PersistKey]` attribute mapping
- [x] Runtime size estimator
- [x] PlayerObject integration helpers
- [x] `OnDataRestored` / `OnDataSaved` lifecycle events
- [x] Compile-time size estimation warnings
- [x] Analyzer: uninitialized synced arrays
- [x] Analyzer: `GetComponent` in Update/FixedUpdate
- [x] Analyzer: oversized sync payloads

#### Phase 3: Workflows ✅

- [x] `UdonTask` / `UdonTask<T>` promise types
- [x] `UdonTask.Delay()`, `UdonTask.Yield()`, `UdonTask.WhenAll()`
- [x] State machine compiler transformation for `async`/`await`
- [x] `[Sync]` attribute with interpolation/quantization options
- [x] `[Rpc]` attribute with target and rate limiting
- [x] `[LocalOnly]` attribute (non-networked methods)
- [x] Full bandwidth estimation per behaviour (Bandwidth Analyzer)

#### Phase 4: Performance ✅

- [x] `[CEComponent]` struct definitions
- [x] Struct-to-SoA compiler transformation
- [x] `CEWorld` entity container with archetype storage
- [x] `[CESystem]` attribute and system registration
- [x] Batched update loop execution
- [x] `CEPool<T>` object pooling
- [x] Spatial partitioning (grid-based)

#### Phase 5: Content & Access ✅

- [x] `CERandom` deterministic PRNG
- [x] `CENoise` (Perlin, Simplex, Worley)
- [x] `CEDungeon` graph-based room generation
- [x] Wave Function Collapse solver (time-sliced)
- [x] CE.Net: late-joiner state reconstruction
- [x] CE.Net: `[SyncOnJoin]` attribute
- [x] CE.Net: conflict resolution helpers
- [x] `[GraphNode]`, `[GraphInput]`, `[GraphOutput]` attributes
- [x] Graph Node Browser (hierarchical tree view, search, details panel)
- [x] Graph Node Code Generator (UdonSharp wrapper generation)
- [x] Graph Node Documentation Generator (Markdown docs)
- [x] Network Simulator (latency, packet loss, jitter, bandwidth)
- [x] Late-Join Simulator (state capture, reconstruction testing)

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
5. **Distributes professionally** via VPM (solving Merlin's prefab warning)

The result enables world types that are currently impractical or impossible:

- **Roguelike RPGs** with procedural dungeons, inventory, and persistent progression
- **RTS games** with hundreds of units and complex AI
- **Story-driven experiences** with cinematic sequences and branching narratives
- **Collaborative creation tools** rivaling standalone applications
- **Competitive multiplayer games** with proper networking architecture
- **Persistent social worlds** with economy, reputation, and customization

**This is not incremental improvement—this is the foundation for the next generation of VRChat experiences.**

---

_UdonSharpCE Enhancement Proposal — Version 2.1 — December 2025_  
_All phases complete — Includes VPM distribution strategy and implementation requirements_
