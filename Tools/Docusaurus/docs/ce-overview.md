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
- `[Sync]`, `[Rpc]`, and `[MergeStrategy]` are analysis helpers; they do not replace normal sync or network event calls.
- `[SyncOnJoin]` and `LateJoinerSync` are scaffolding; serialization and application are placeholders.
- `CEPersistence.Save` and `CEPersistence.Restore` are placeholders; use `ToData`/`FromData` or JSON helpers for now.
- Optimization attributes like `[CEInline]` and `[CENoOptimize]` are declared but not yet enforced (only `[CEPreserveAction]` is active).
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
    [Sync] public int score; // No runtime sync without UdonSynced
}
```

### Good

```csharp
using UdonSharp;
using UdonSharp.CE.Net;

public class Scoreboard : UdonSharpBehaviour
{
    [UdonSynced]
    [Sync(InterpolationMode.None)]
    public int score;
}
```
