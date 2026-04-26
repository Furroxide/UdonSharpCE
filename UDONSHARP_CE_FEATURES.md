# UdonSharp CE - Feature Reference

Version: 1.1
Last Updated: 2026-04-26

This document summarizes the implemented UdonSharp CE features and their current status. For step-by-step usage and code examples, see the Docusaurus docs:

- Tools/Docusaurus/docs/ce-overview.md
- Tools/Docusaurus/docs/ce-core.md
- Tools/Docusaurus/docs/ce-async.md
- Tools/Docusaurus/docs/ce-data.md
- Tools/Docusaurus/docs/ce-net.md
- Tools/Docusaurus/docs/ce-persistence.md
- Tools/Docusaurus/docs/ce-devtools.md
- Tools/Docusaurus/docs/ce-perf.md
- Tools/Docusaurus/docs/ce-procgen.md
- Tools/Docusaurus/docs/ce-graphbridge.md
- Tools/Docusaurus/docs/ce-netphysics.md
- Tools/Docusaurus/docs/ce-editor-tools.md

## Status Legend

- Implemented: Feature is active and used in runtime or editor tooling.
- Partial: Feature is present but has important limitations.
- Placeholder: API exists but behavior is not wired or enforced yet.

## Module Summary

| Module | Status | Notes |
| --- | --- | --- |
| CE.Core | Implemented | CECallback is active; most optimization attributes are placeholders. |
| CE.Async | Partial | Delay/Yield awaits work; result-awaiting is not wired. |
| CE.Data | Implemented | CEList, CEDictionary, and manual data bridge converters. |
| CE.Net | Partial | `[Sync]` uses the upstream Udon synced-variable pipeline; `[SyncOnJoin]` emits late-join glue for supported data graphs. |
| CE.Persistence | Partial | Conversion, validation, and PlayerData-backed Save/Restore are wired; quota introspection remains limited. |
| CE.DevTools | Implemented | Runtime logging, console, profiler, and monitor utilities. |
| CE.Perf | Implemented | ECS-lite world, pools, grids, LOD, batching. |
| CE.Procgen | Implemented | Deterministic RNG, noise, dungeon, and WFC tools. |
| CE.GraphBridge | Implemented | Attributes and editor generators for graph nodes. |
| CE.NetPhysics | Implemented (beta) | Predictive physics with input streaming and snapshots. |
| Editor Tools | Implemented | Analyzers, optimizers, inspector, and windows. |

## CE.Core

Key APIs:

- CECallback: parameterless callback abstraction (`Target`, `MethodName`, `Invoke`).
- CEPreserveAction: opt out of Action-to-CECallback transformation.
- Optimization attributes: CENoOptimize, CENoInline, CEInline, CENoUnroll, CEUnroll, CEConst, CEDebugOnly (declared for future enforcement).

Limitations:

- Only parameterless Action is transformed; Action<T> and closures are not supported.
- Only CEPreserveAction is enforced by the compiler pipeline today.

## CE.Async

Key APIs:

- UdonTask and UdonTask<T> with Delay, DelayFrames, Yield, WhenAll, WhenAny helpers.
- CancellationToken and CancellationTokenSource for cooperative cancellation.

Limitations:

- The transformer supports await on Delay, DelayFrames, and Yield only.
- Awaiting tasks that return values is not implemented.
- WhenAll and WhenAny return placeholder tasks; they are not awaited correctly yet.

## CE.Data

Key APIs:

- CEList<T>: array-backed list with DataList and JSON helpers.
- CEDictionary<TKey, TValue>: dictionary with DataDictionary and JSON helpers.
- CEDataBridge: manual converters between C# models and DataDictionary/JSON.
- DataModel, DataField, DataIgnore attributes (manual registration required).

Limitations:

- Default DataToken conversions only cover primitives, string, DataList, DataDictionary.
- CEList<T> requires T : IComparable under Udon compilation.
- JSON numbers deserialize as doubles; explicit casting is required.

## CE.Net

Key APIs:

- Sync, SyncOnJoin, SyncSerializer attributes.
- Rpc, RpcOwnerOnly, LocalOnly attributes.
- RateLimiter, NetworkLimits, ConflictResolver, MergeStrategyAttribute.

Limitations:

- `[Sync]` inherits `UdonSyncedAttribute`, so it participates in UdonSharp's normal synced-variable pipeline. Do not apply both `[Sync]` and `[UdonSynced]` to the same field.
- `Quantize` rounds float-compatible values before serialization. `DeltaEncode` / `DeltaEncodeHint` is advisory only; VRChat does not expose true delta sync for Udon fields.
- `[SyncOnJoin]` emits JSON glue for primitives, enums, strings, supported Unity value structs, `DataDictionary`/`DataList`, arrays, `List<T>`, and Unity-serializable pure data models. Scene/object references and cyclic or polymorphic graphs require `[SyncSerializer]`; CE0032 warns when generated glue is omitted.
- `[Rpc]` is still an analyzer/documentation helper; it does not send network events automatically.

## CE.Persistence

Key APIs:

- PlayerData, PersistKey, PersistIgnore attributes.
- Range, MaxLength, Required validation attributes.
- CEPersistence Register/Save/Restore/ToData/FromData/ToJson/FromJson.
- PersistenceLifecycle callbacks and PlayerObjectHelper slot mapping.

Limitations:

- Save serializes registered model `DataDictionary` JSON into VRChat PlayerData with `PlayerData.SetString`; Restore reads it with `PlayerData.TryGetString`.
- Restore can return `NotReady` before local PlayerData is available and `NoData` when no key has been stored yet.
- Auto-registration supports primitives, strings, DataList/DataDictionary, and arrays of those types; unsupported fields require manual converters.
- `GetRemainingQuota()` still returns the full quota because VRChat does not expose per-key usage.

## CE.DevTools

Key APIs:

- CELogger with log levels and tags.
- CEDebugConsole for in-world log display.
- CEProfiler for timing and section profiling.
- PerformanceMonitor for lightweight FPS monitoring.

Limitations:

- CELogger calls are transformed into Debug.Log during Udon compilation.
- Filtering and console integration require custom wiring in compiled Udon.

## CE.Perf

Key APIs:

- CEWorld ECS-lite container, CEQuery, and ComponentTypeId.
- CEPool<T> for object pooling with O(1) handles.
- CEGrid for spatial partitioning.
- CELod for distance-based update frequency.
- BatchProcessor for time-sliced processing.

Limitations:

- CEComponent and CESystem attributes are metadata only; CEWorld does not auto-register them yet.

## CE.Procgen

Key APIs:

- CERandom: deterministic RNG with shuffle and weighted choice.
- CENoise: Perlin, Simplex, Worley, and fractal variants.
- CEDungeon: layout generation with rooms and corridors.
- WFCSolver: wave function collapse solver with incremental Tick.

## CE.GraphBridge

Key APIs:

- GraphNode, GraphInput, GraphOutput, GraphFlowOutput attributes.
- GraphProperty and GraphEvent attributes.
- GraphCategory and GraphTypeConstraint attributes.

Editor tooling:

- Node Browser, Generate Nodes, Generate Wrappers, Generate Documentation, Validate Nodes.

## CE.NetPhysics

Key APIs:

- NetPhysicsWorld: tick clock, entity registry, state sync.
- NetPhysicsEntity: base class for networked objects.
- NetVehicle and NetBall sample entities.
- InputRecorder, InputBuffer, InputPredictor for input streaming.
- FrameHistory, RollbackManager, StateCompressor, InterestManager.

Limitations:

- State packets are capped to 8 entities per send to stay within sync limits.
- NetVehicle expects a Rigidbody and uses physics forces for motion.

## Editor Tools

Analyzers (examples):

- CE0001: Uninitialized synced arrays.
- CE0002: GetComponent in Update hot paths.
- CE0003: Continuous sync payload size.
- CE0010: Sync attribute validation.
- CE0011: RPC signature validation.
- CE0012: LocalOnly network misuse.
- CE0032: SyncOnJoin field requires a custom serializer for generated late-join glue.
- CE0020: Async method validation.
- CE0030: PlayerData size estimation.

Optimizers (examples):

- Async method transformer.
- Action to CECallback transformer.
- CELogger call transform.
- Loop invariant code motion, loop unrolling, CSE, inlining, extern call caching, string interning.

Inspector and windows:

- CE Inspector with property grouping and optimization info.
- Bandwidth Analyzer, World Validator, Network Simulator, Late-Join Simulator.

## UdonSharp Subset Reminders

- Avoid named arguments and reflection.
- Initialize synced arrays at declaration time.
- Prefer simple data structures and arrays over complex generic or struct-heavy patterns.
