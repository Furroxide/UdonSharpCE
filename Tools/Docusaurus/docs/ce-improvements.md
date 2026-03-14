# UdonSharp Community Edition — What's New

UdonSharp CE is a drop-in replacement for standard UdonSharp that adds high-level features and addresses long-standing pain points. This guide highlights what matters most to world creators.

---

## Networking & Sync Fixes

The standard UdonSharp experience is plagued by subtle networking bugs that cause hours of debugging. CE tackles these head-on with compile-time analyzers that catch problems _before_ you upload.

### Uninitialized Synced Arrays (CE0001)

**The Problem:** Declaring a synced array without initializing it causes sync to fail silently. Your variables just... don't update for other players. No error, no warning—just broken networking.

```csharp
// ❌ Standard U# compiles this without complaint, but sync SILENTLY FAILS
[UdonSynced] public int[] scores;
```

**CE Solution:** The compiler errors immediately, telling you exactly what's wrong.

```csharp
// ✅ CE requires initialization
[UdonSynced] public int[] scores = new int[16];
```

### Oversized Sync Payloads (CE0003)

**The Problem:** Continuous sync has a ~200 byte limit. Exceed it and you get "death runs"—data loss with no indication of what went wrong.

```csharp
// ❌ This is ~400 bytes and will cause silent data loss
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class TooBig : UdonSharpBehaviour
{
    [UdonSynced] public float[] positions = new float[100];
}
```

**CE Solution:** The compiler warns you when your sync payload is too large, with an estimate of actual byte usage.

### Late-Joiner State Reconstruction

**The Problem:** Players joining mid-session often see broken state because there's no built-in way to sync initial values properly.

**CE Solution:**

- `[SyncOnJoin]` attribute marks fields that need late-join handling
- **Late-Join Simulator** editor tool lets you test reconstruction without leaving Unity
- `LateJoinerSync` helpers for manual state reconstruction patterns

### RPC & Sync Validation (CE0010, CE0011, CE0012)

**The Problem:** Invalid RPC signatures, wrong sync modes, or calling network events on local-only methods all compile fine but break at runtime.

**CE Solution:** Compile-time validation catches:

- RPC methods with unsupported parameter types
- Sync attributes that conflict with the behaviour's sync mode
- Network calls to methods marked `[LocalOnly]`

### Bandwidth Analyzer

**The Problem:** "How much bandwidth am I actually using?" is unanswerable without careful manual calculation.

**CE Solution:** Open `Udon CE/Dev Tools/Bandwidth Analyzer` to see:

- Per-behaviour sync payload sizes
- Estimated bandwidth usage
- Warnings when approaching VRChat's 11 KB/s budget

### Network Simulator

**The Problem:** Testing networking requires multiple clients, making iteration painfully slow.

**CE Solution:** `Udon CE/Dev Tools/Network Simulator` lets you simulate:

- Latency (ping)
- Packet loss
- Jitter
- Bandwidth throttling

Test edge cases without leaving the editor.

---

## Async/Await Support

Write time-based code without callback hell or manual state machines.

```csharp
public async UdonTask PlayCutscene()
{
    await FadeToBlack(1.0f);
    await UdonTask.Delay(2f);
    await ShowDialogue("Welcome, traveler...");
    await UdonTask.Yield(); // Wait one frame
    EnablePlayerControls();
}
```

**What works:**

- `await UdonTask.Delay(seconds)` — wait for time
- `await UdonTask.DelayFrames(count)` — wait for frames
- `await UdonTask.Yield()` — wait one frame
- `CancellationToken` for interruptible flows

No more nested `SendCustomEventDelayedSeconds` chains.

---

## Type-Safe Collections

Proper generic collections with VRChat data container interop.

### CEList&lt;T&gt;

```csharp
private CEList<int> _scores = new CEList<int>();

void AddScore(int points)
{
    _scores.Add(points);
    Debug.Log($"Total scores: {_scores.Count}");

    // Easy JSON export
    string json = _scores.ToJson();
}
```

### CEDictionary&lt;TKey, TValue&gt;

```csharp
private CEDictionary<string, int> _inventory = new CEDictionary<string, int>();

void AddItem(string item)
{
    if (_inventory.ContainsKey(item))
        _inventory[item]++;
    else
        _inventory[item] = 1;
}
```

Both collections convert seamlessly to/from VRChat's `DataList` and `DataDictionary`.

---

## Persistence Layer

Structured approach to VRChat's PlayerData system.

```csharp
[PlayerData("my_save")]
public class PlayerSaveData
{
    [PersistKey("xp")] public int experience;
    [PersistKey("lvl"), Range(1, 100)] public int level = 1;
    [PersistKey("inv")] public int[] inventory = new int[50];
}
```

**Features:**

- Attribute-based field mapping
- Built-in validation (`[Range]`, `[MaxLength]`, `[Required]`)
- **Size estimation** — know if you're approaching the 100KB limit
- Lifecycle callbacks for save/restore events

---

## Performance Tools

Build worlds with hundreds of dynamic objects.

### ECS-Lite World

Data-oriented entity management for high object counts:

```csharp
private CEWorld _world;
private Vector3[] _positions;
private Vector3[] _velocities;

void Start()
{
    _world = new CEWorld(512);
    _positions = new Vector3[512];
    _velocities = new Vector3[512];

    _world.RegisterComponent(PositionType, _positions);
    _world.RegisterComponent(VelocityType, _velocities);
    _world.RegisterSystem(this, nameof(MoveEntities));
}

public void MoveEntities()
{
    int count = _world.ActiveEntityCount;
    for (int i = 0; i < count; i++)
    {
        _positions[i] += _velocities[i] * Time.deltaTime;
    }
}
```

### Object Pooling

O(1) acquire/release for spawnable objects:

```csharp
private CEPool<Projectile> _bulletPool;

void Fire()
{
    var bullet = _bulletPool.Acquire();
    bullet.transform.position = _muzzle.position;
    // ... later ...
    _bulletPool.Release(bullet);
}
```

### Spatial Queries

Fast proximity lookups:

```csharp
private CEGrid _grid;

int[] nearby = new int[32];
int count = _grid.QueryRadius(playerPos, 10f, nearby);
```

### LOD System

Distance-based update frequency:

```csharp
private CELod _lod;

void Update()
{
    if (_lod.ShouldUpdate(transform.position, _player.position))
    {
        // Only runs when player is close enough
        DoExpensiveUpdate();
    }
}
```

---

## Networked Physics (Beta)

Full framework for client-predicted, server-authoritative physics.

**Use cases:**

- Racing games
- Sports games (e.g., Rocket League-style)
- Any physics-heavy multiplayer

**Components:**

- `NetPhysicsWorld` — tick-based simulation coordinator
- `NetVehicle` — vehicle controller with prediction
- `NetBall` — shared physics object
- `InputRecorder` / `InputBuffer` — input streaming
- `RollbackManager` — state correction
- `StateCompressor` — bandwidth-efficient snapshots

---

## Procedural Generation

Deterministic generation that syncs across clients.

### CERandom

```csharp
var rng = new CERandom(seed); // Same seed = same results everywhere
float value = rng.Range(0f, 1f);
int[] shuffled = rng.Shuffle(myArray);
```

### CENoise

```csharp
CENoise.Initialize(seed);
float height = CENoise.Fractal2D(x, z, octaves: 4);
```

Includes Perlin, Simplex, Worley, and fractal variants.

### CEDungeon

Graph-based room generation for roguelike layouts.

### WFCSolver

Wave Function Collapse for tile-based generation, with time-slicing to avoid frame drops.

---

## Developer Tools

### In-World Debug Console

Display logs inside your world without external tools.

### CEProfiler

```csharp
profiler.BeginSection("AI");
// ... AI code ...
profiler.EndSection();

// Later
Debug.Log(profiler.GetSummary());
```

### CELogger

Structured logging with levels and tags:

```csharp
CELogger.Info("Player spawned", "Spawn");
CELogger.Warning("Low health", "Combat");
CELogger.Error("Save failed", "Persistence");
```

### World Validator

Pre-publish checks accessible via `Udon CE/Dev Tools/World Validator`:

- GetComponent calls in Update loops
- Uninitialized synced arrays
- Invalid VRCPlayerApi usage
- Bandwidth limit violations
- Persistence size warnings

---

## Compile-Time Optimizations

CE automatically optimizes your code during compilation:

| Optimization                     | What It Does                               |
| -------------------------------- | ------------------------------------------ |
| Loop Invariant Code Motion       | Moves unchanging calculations out of loops |
| Small Loop Unrolling             | Unrolls tiny fixed-iteration loops         |
| Common Subexpression Elimination | Reuses repeated calculations               |
| Tiny Method Inlining             | Inlines small private methods              |
| Extern Call Caching              | Caches repeated SDK property lookups       |
| String Interning                 | Deduplicates identical string literals     |

You don't need to do anything—these apply automatically.

---

## Graph Bridge

Expose your U# code to Udon Graph users:

```csharp
[GraphNode("Utilities/Math")]
public static class MathNodes
{
    [GraphOutput("result")]
    public static float Lerp(
        [GraphInput] float a,
        [GraphInput] float b,
        [GraphInput] float t)
    {
        return Mathf.Lerp(a, b, t);
    }
}
```

Editor tools generate wrapper nodes and documentation automatically.

---

## Installation

UdonSharp CE installs via VPM (VRChat Creator Companion):

1. Open VCC → Settings → Packages → Add Repository
2. Add: `https://charlvs.github.io/vpm/index.json`
3. Open your project → Install "UdonSharp Community Edition"

CE declares compatibility with standard UdonSharp, so existing prefabs work without changes.

---

## Summary

| Category         | Standard U# Pain Point | CE Solution                                        |
| ---------------- | ---------------------- | -------------------------------------------------- |
| **Networking**   | Silent sync failures   | Compile-time analyzers (CE0001, CE0003, CE0010-12) |
| **Late Joiners** | Broken initial state   | `[SyncOnJoin]` + Late-Join Simulator               |
| **Bandwidth**    | Unknown usage          | Bandwidth Analyzer tool                            |
| **Timing**       | Callback spaghetti     | `async`/`await` with `UdonTask`                    |
| **Collections**  | Raw arrays only        | `CEList<T>`, `CEDictionary<K,V>`                   |
| **Persistence**  | Manual serialization   | `[PlayerData]` attribute mapping                   |
| **Performance**  | Entity limits          | ECS-lite, pooling, spatial grids                   |
| **Debugging**    | Print debugging        | In-world console, profiler, validators             |
| **Testing**      | Multi-client required  | Network Simulator, Late-Join Simulator             |

---

**UdonSharp CE: Stop fighting the tooling. Start building your world.**
