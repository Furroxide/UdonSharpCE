# Community Edition Overview

UdonSharp Community Edition (CE) adds runtime libraries and editor tooling that extend UdonSharp with higher-level C# APIs. CE focuses on productivity and performance while staying inside the UdonSharp supported subset.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Async;
using UdonSharp.CE.Data;
using UnityEngine;

public class CEQuickStart : UdonSharpBehaviour
{
    private CEList<int> _scores = new CEList<int>();

    public async UdonTask RunRound()
    {
        _scores.Add(1);
        await UdonTask.Delay(2f);
        Debug.Log("Scores: " + _scores.Count);
    }
}
```

## Modules At A Glance

| Module | What it provides | Docs |
| --- | --- | --- |
| CE.Core | Callback helpers and compiler-facing attributes | [CE Core](ce-core) |
| CE.Async | Async flow helpers and UdonTask | [CE Async](ce-async) |
| CE.Data | CEList, CEDictionary, data bridges, JSON helpers | [CE Data](ce-data) |
| CE.Net | Sync/RPC annotations, analysis helpers, rate limiting | [CE Net](ce-net) |
| CE.Persistence | Player data models, validation, size estimation | [CE Persistence](ce-persistence) |
| CE.DevTools | Runtime logger, debug console, profiler | [CE DevTools](ce-devtools) |
| CE.Perf | ECS-lite world, pooling, grids, LOD, batching | [CE Perf](ce-perf) |
| CE.Procgen | Deterministic RNG, noise, dungeon and WFC tools | [CE Procgen](ce-procgen) |
| CE.GraphBridge | Attributes and generators for graph nodes | [CE GraphBridge](ce-graphbridge) |
| CE.NetPhysics | Networked physics framework | [CE NetPhysics](ce-netphysics) |
| Editor Tools | Analyzers, optimizers, inspector, menu tools | [CE Editor Tools](ce-editor-tools) |

## Feature Status Notes

- Async transformation currently supports `await UdonTask.Delay`, `DelayFrames`, and `Yield`. Awaiting tasks that return values or `WhenAll/WhenAny` is not yet implemented.
- `UdonTask<T>` exists, but awaited result assignment is not wired in the current transformer.
- `[Sync]` participates in UdonSharp's normal synced-variable pipeline by inheriting `UdonSyncedAttribute`. Do not combine `[Sync]` and `[UdonSynced]` on the same field.
- `[Sync]` now requires an `Authority` as its first positional argument: `[Sync(Authority.Owner)]` for state one client decides and the rest are told about, `[Sync(Authority.Predicted)]` for state every client simulates locally and the owner's packet corrects. This is a breaking change — bare `[Sync]` and `[Sync(InterpolationMode.X)]` no longer compile, and `Authority.Owner` is the behaviour-preserving migration for both. Interpolation is now the second positional argument or a named one. See [CE Net](ce-net#sync-authority).
- `[UdonSynced]` is unchanged and fully supported; it has no authority parameter and needs no migration. The difference is precision, not capability: CE0014 infers authority for a bare `[UdonSynced]` field from the code around the write, and a declared `Authority` replaces that inference with a statement. CE0014 is authority-aware as a result, and CE0063 warns when an `Authority.Predicted` field lives on a behaviour whose declared sync mode replicates no variables at all, so there is no owner value it could ever reconcile against. CE0063 stops at that provable case rather than guessing whether a Manual behaviour publishes, and CE0033 rejects that combination during codegen before analysis runs, so CE0063 is a backstop rather than a routine check — declaring a field `Predicted` turns CE0014 off and takes the owner's reconciliation on trust. See [CE Editor Tools](ce-editor-tools#compile-time-analyzers).
- `[SyncOnJoin]` emits generated JSON glue for supported pure data graphs and carries it in an `[UdonSynced]` string on the behaviour itself; the behaviour is forced to Manual sync. Scene/object references, polymorphic data, and cyclic graphs require `[SyncSerializer]`; CE0032 warns when generated glue is omitted.
- `CEPersistence.Save` and `CEPersistence.Restore` use VRChat PlayerData string storage through JSON. Models are auto-registered from `[PlayerData]`/`[PersistKey]` at compile time.
- `DeltaEncode` is advisory only; use manual sync plus a custom payload for true delta-style updates.
- The optimizer opt-out attributes (`[CENoOptimize]`, `[CENoInline]`, `[CEInline]`, `[CENoUnroll]`, `[CEUnroll]`, `[CEConst]`, `[CEDebugOnly]`) are now `[Obsolete(error: true)]`. The syntactic optimizers they exempted from are all disabled by default — each was found able to change or delete program behaviour — so there is nothing left to opt out of, and an attribute that silently does nothing is the failure class this fork removes. `[CEPreserveAction]` is still active and still read.
- `[CEComponent]` and `[CESystem]` are metadata only; CEWorld does not auto-register them yet.
- `CELogger` calls are transformed to `Debug.Log` in Udon compilation; in-world console integration requires manual wiring.

## UdonSharp Subset Reminders

- Avoid named arguments and reflection.
- Initialize synced arrays at declaration time.
- Prefer simple classes and arrays over complex generic or struct-heavy designs.

## Common Pitfalls

### Bad

```csharp
using UdonSharp.CE.Net;

public class Scoreboard : UdonSharpBehaviour
{
    [UdonSynced]
    [Sync(Authority.Owner)] public int score; // Invalid: choose one sync attribute

    [Sync] public int lives;                  // Invalid: Authority is required
}
```

### Good

```csharp
using UdonSharp;
using UdonSharp.CE.Net;

public class Scoreboard : UdonSharpBehaviour
{
    [Sync(Authority.Owner)]
    public int score;
}
```
