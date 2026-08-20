# UdonSharp CE

**Community Edition fork of [UdonSharp](https://github.com/MerlinVR/UdonSharp)** — higher-level C#
for VRChat Udon.

Udon has no generics, no collections, and gives you no feedback when it silently discards a synced
write from a non-owner. CE adds runtime libraries and editor tooling that close those gaps while
staying inside the UdonSharp supported subset.

📖 **[Documentation](https://vrc.furroxide.dev)** · [Community Edition
overview](https://vrc.furroxide.dev/ce-overview)

---

## What CE adds

- **Collections where Udon has none** — `CEList<T>`, `CEDictionary`, data bridges and JSON helpers.
- **Compile-time analysis of networking correctness** — VRChat discards synced writes from
  non-owners silently, so the bug surfaces hours later in a live instance as "the score is wrong on
  one client". CE's analyzers catch that class of mistake at compile time, along with unserialized
  manual-sync behaviours, saturated serialization rates, and ownership mistakes.
- **Declared sync authority** — `[Sync(Authority.Owner)]` vs `[Sync(Authority.Predicted)]` states at
  the field who may write it, so predict-and-reconcile code is no longer reported as a bug.
- **Networked physics** — a rollback-based framework with input streaming and snapshots.
- **Performance tooling** — an ECS-lite world, pooling, grids, LOD and batching, plus compiler
  optimizers.

## Modules

| Module | Status | What it provides |
| --- | --- | --- |
| CE.Core | Implemented | Callback helpers and compiler-facing attributes |
| CE.Data | Implemented | `CEList`, `CEDictionary`, data bridges, JSON helpers |
| CE.DevTools | Implemented | Runtime logger, debug console, profiler |
| CE.Perf | Implemented | ECS-lite world, pooling, grids, LOD, batching |
| CE.Procgen | Implemented | Deterministic RNG, noise, dungeon and WFC tools |
| CE.GraphBridge | Implemented | Attributes and generators for graph nodes |
| Editor Tools | Implemented | Analyzers, optimizers, inspector, menu tools |
| CE.NetPhysics | Implemented (beta) | Networked physics framework |
| CE.Net | Partial | Sync/RPC annotations, analysis helpers, rate limiting |
| CE.Persistence | Partial | Player data models, validation, size estimation |
| CE.Async | **Partial** | `UdonTask`; `Delay`/`Yield` awaits work — **result-awaiting is not wired** |

Status is tracked in [`UDONSHARP_CE_FEATURES.md`](UDONSHARP_CE_FEATURES.md). Please read the status
column before depending on a module — `Partial` means exactly what it says.

## Install

Add the VPM listing in the VRChat Creator Companion:

```
https://vrc.furroxide.dev/vpm/index.json
```

Then add **UdonSharp CE** to your world project. See
[the install guide](https://vrc.furroxide.dev/ce-improvements) for details.

> **Note:** CE replaces UdonSharp in a project — do not install both.

## Relationship to upstream

This is a fork of **[MerlinVR/UdonSharp](https://github.com/MerlinVR/UdonSharp)** by Merlin, and
carries its full history. CE tracks upstream and keeps the compiler's behaviour compatible; the CE
additions live under `Packages/com.merlin.UdonSharp/{Runtime/Libraries,Editor}/CE/`.

**Report CE issues here.** For bugs in UdonSharp itself, or in the VRChat SDK, use
[vrchat-community/UdonSharp](https://github.com/vrchat-community/UdonSharp) instead —
[`Upstream-Risk-Changes`](https://vrc.furroxide.dev/upstream-risk-changes) documents where CE
diverges.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). There is no CLA.

## License

[MIT](LICENSE). UdonSharp is © Merlin and UdonSharp Contributors, and © VRChat Inc.; CE additions
are © UdonSharp CE Contributors. All original copyright notices are retained.
