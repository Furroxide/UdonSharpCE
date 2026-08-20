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
- Authority enum (`Owner`, `Predicted`) — the required first argument to `[Sync]`.
- Rpc, RpcOwnerOnly, LocalOnly attributes.
- RateLimiter, NetworkLimits, ConflictResolver, MergeStrategyAttribute.

Behaviour sync mode is **inferred**. A behaviour with synced state and no `[UdonBehaviourSyncMode]`
gets one injected at compile time — Manual when something requires it (synced arrays, `[SyncOnJoin]`
payloads, synced strings), Continuous otherwise. A mode you declared yourself is never silently
overridden; a conflicting one is a compile error (CE0033 for `None`/`NoVariableSync` with synced
state, CE0034 for `Continuous` with Manual-only state).

`[SyncOnJoin]` transports state as an `[UdonSynced]` string on the behaviour itself, written in
`OnPreSerialization` and applied in `OnDeserialization`. Authority is normal object ownership and
VRChat delivers the value to late joiners automatically. There is no coordinator object. `Group` gives
a behaviour one such string per group instead of one in total.

Limitations:

- `[Sync]` inherits `UdonSyncedAttribute`, so it participates in UdonSharp's normal synced-variable pipeline. Do not apply both `[Sync]` and `[UdonSynced]` to the same field.
- `[Sync]` requires an `Authority` as its first positional argument. `Authority.Owner` means only the
  owner may write the field; a write on any other client is not rejected or logged, it simply never
  leaves that client and is overwritten by the owner's next deserialization. `Authority.Predicted`
  means every client writes the field as local simulation and the owner's value is the one that
  wins. `Owner` is `0`, so the defaulted reading is the strict one.
- **Breaking change:** `Authority` being required makes bare `[Sync]`, `[Sync(InterpolationMode.X)]`
  and `[Sync(Quantize = x)]` compile errors. Interpolation is now the second positional argument or a
  named one: `[Sync(Authority.Owner)]`, `[Sync(Authority.Owner, InterpolationMode.Linear)]`,
  `[Sync(Authority.Owner, Interpolation = InterpolationMode.Linear)]`,
  `[Sync(Authority.Owner, Quantize = 0.01f)]`. `Authority.Owner` is the behaviour-preserving
  migration for every existing call site, because CE0014 already treated every synced field as
  owner-authoritative. `[UdonSynced]` is unchanged, still fully supported, and needs no migration —
  the difference is precision, not capability: a declared authority replaces the inference CE0014
  otherwise falls back on.
- `Authority.Predicted` exempts the field from CE0014, and nothing takes CE0014's place. Declaring a
  field predicted is a claim that the owner reconciles it, and that claim is taken on trust: CE0063
  reports only a predicted field on a behaviour whose declared sync mode replicates no variables at
  all (`BehaviourSyncMode.None` or `NoVariableSync`), where the prediction is unreconcilable whatever
  any other code does. It does not examine `Manual` or `Continuous` behaviours, because
  `RequestSerialization()` can be reached from an ungated `Update` (which runs on the owner too), a
  base class, another behaviour, or an inspector-wired `SendCustomEvent`, none of which are provable
  from source. CE0033 normally rejects the one case CE0063 does cover during codegen, so CE0063 is a
  backstop. Authority is also declared per field, not per write, so a predicted field's genuinely
  owner-only writes lose CE0014 coverage as well.
- `Quantize` rounds float-compatible values before serialization. `DeltaEncode` / `DeltaEncodeHint` is advisory only; VRChat does not expose true delta sync for Udon fields.
- `[SyncOnJoin]` emits JSON glue for primitives, enums, strings, supported Unity value structs, `DataDictionary`/`DataList`, arrays, `List<T>`, and Unity-serializable pure data models. Scene/object references and cyclic or polymorphic graphs require `[SyncSerializer]`; CE0032 warns when generated glue is omitted.
- `List<T>` is supported. UdonSharp's internal `List<T>` declares `where T : IComparable` under
  `COMPILER_UDONSHARP`, but that define is only applied to the compiler's private parse options — the
  constraint is not in `UdonSharp.Runtime.dll` metadata and the generated glue names the BCL `List<T>`, so
  `List<Vector3>`, `List<Color>` and `List<YourModel>` all work.
- `[SyncOnJoin]` forces its behaviour to Manual sync, and VRChat requires all synced UdonBehaviours on one GameObject to share a sync method. An interpolated `[Sync(Authority.Owner, InterpolationMode.Linear)] Vector3` (Continuous) and a `[SyncOnJoin]` payload (Manual) therefore cannot coexist on one GameObject — split them across objects.
- `Priority` orders fields **within** one payload, so it only relates fields that share a `Group`. Two groups are two synced variables that VRChat may deliver in either order, and cross-behaviour late-join ordering is not achievable at all: VRChat delivers each behaviour's deserialization independently. Express any dependency that has to hold as data (a phase/version field the dependent behaviour checks), or keep both fields in one group.
- `[Rpc]` sends. The receiver is marked `[NetworkCallable]` so VRChat enforces the declared `RateLimit`,
  `OwnerOnly` becomes a real receiver-side ownership check via `NetworkCalling.CallingPlayer`, and calls are
  rewritten into `SendCustomNetworkEvent`. `RpcTarget.Self` stays a direct call; an `[Rpc]` calling another
  `[Rpc]` is CE0045 (direct self-recursion is allowed) — use `CENet.Rpc(...)` for a deliberate hop.
  Parameters must be Udon-serializable, at most 8, non-optional; the method must return `void`.
- `SyncOnJoin.Group` splits a behaviour's late-join state across payloads: fields sharing a group share one
  `[UdonSynced]` string with its own capture, restore and applied-guard, and fields naming no group share the
  default payload, which keeps the names it has always had. Capture asks VRChat to serialize only when some
  group's JSON actually changed, so a large cold snapshot stops going back on the wire every time a small hot
  field moves. Grouping needs no second GameObject, which splitting across behaviours did — VRChat requires
  every synced UdonBehaviour on one GameObject to share a sync method. The name is used to build generated
  member names, so characters outside `a-z A-Z 0-9 _` become underscores and two names that differ only in
  punctuation are a compile error rather than a silent merge back into one payload.
- `SyncOnJoin.Compress`, `.Critical` and `Sync.Priority` are `[Obsolete(error: true)]` — nothing ever read
  them. `Critical` is not implementable at all: VRChat reports serialization success per behaviour, not per
  variable.
- The optimizer opt-out attributes (`[CENoOptimize]`, `[CENoInline]`, `[CEInline]`, `[CENoUnroll]`,
  `[CEUnroll]`, `[CEConst]`, `[CEDebugOnly]`) are `[Obsolete(error: true)]`: the syntactic optimizers they
  exempted from are all disabled by default, so there is nothing to opt out of. `[CEPreserveAction]` is live.

## CE.Persistence

Key APIs:

- PlayerData, PersistKey, PersistIgnore attributes.
- Range, MaxLength, Required validation attributes.
- CEPersistence `Save`/`Restore`/`ToData`/`FromData`/`ToJson`/`FromJson`/`Validate`/`EstimateSize`, generated per `[PlayerData]` model at compile time and called with the model type directly.
- PersistenceLifecycle result records (`GetPendingSaveResult`, `GetPendingRestoreResult`, `GetPendingModelKey`, `GetPendingCorruptionInfo`) and PlayerObjectHelper slot mapping.
- Quota queries: `GetQuotaLimit()`, `GetUsedQuota()`, `GetRemainingQuota()`, `GetWritableQuota(key)`.

Limitations:

- `[PlayerData]` is the only registration path under Udon. `CEPersistence.Register<T>()` and the other generic entry points are editor-only and are marked `[Obsolete(error: true)]` for the Udon compile, so calling them from behaviour code fails the build naming the generated overload to use instead.
- The cross-behaviour `PersistenceLifecycle` callback registry (`RegisterCallback`, `IPersistenceCallbacks`) was removed: UdonSharp emits the statics of a non-behaviour type into each calling behaviour's own program heap, so a callback registered in behaviour A was invisible to a save issued from behaviour B. Read the `SaveResult`/`RestoreResult` returned in the calling behaviour, take corruption detail from `GetPendingCorruptionInfo()` there, and forward the outcome to other behaviours with `SendCustomEvent`.
- Save serializes the model `DataDictionary` as JSON into VRChat PlayerData with `PlayerData.SetString`; Restore reads it with `PlayerData.TryGetString`.
- Restore can return `NotReady` before local PlayerData is available and `NoData` when no key has been stored yet.
- Auto-registration supports primitives, strings, DataList/DataDictionary, and arrays of those types; unsupported fields require manual converters.
- Quota figures come from `Networking.GetPlayerDataStorageLimit`/`GetPlayerDataStorageUsage`, so they are account-wide totals, not per-key. `GetWritableQuota(key)` is the budget Save enforces against: remaining quota plus an estimate of the bytes that key already holds, since the write replaces them. Both APIs need a running client — they fall back to the 100KB constant and `-1` in edit mode — and ClientSim's usage number is approximate and logs a warning on every query, so cache it rather than polling per frame.

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

`NetVehicle` is the worked example of `[Sync(Authority.Predicted)]`. `_boostRemaining` and
`_jumpTimer` are written by every client, because `NetPhysicsWorld` steps every registered vehicle
on every client — that is what keeps a remote car's boost meter and dodge timing moving between
packets instead of stepping only when one lands. The owner's `RequestSerialization` publishes the
real values and `OnDeserialization` replaces the local guesses, which bounds the drift.
`_ownerPlayerId` is `[Sync(Authority.Predicted)]` too, for a different reason: every client latches
the driver id out of the `AssignDriver` broadcast so input routing starts on the frame the assignment
is heard instead of stalling for the owner's next serialization, and the owner republishes it
afterwards. Authority records who may write the field, not where the local value came from, so a
value that arrives in a broadcast is as predicted as one produced by integration.
`_airStateFlags` is the only `[Sync(Authority.Owner)]` field on the vehicle: its single write is in
`OnPreSerialization`, which runs on the serializing client and nowhere else.
A boost-pad grant still runs on the owner even though `_boostRemaining` is predicted — a grant is
new information no client can compute for itself, not a prediction.

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
- CE0014: Synced field written with no ownership check. Authority-aware — strict for
  `[Sync(Authority.Owner)]`, inferred for bare `[UdonSynced]`, skipped for
  `[Sync(Authority.Predicted)]`.
- CE0032: SyncOnJoin field requires a custom serializer for generated late-join glue.
- CE0020: Async method validation.
- CE0030: PlayerData size estimation.
- CE0063: `[Sync(Authority.Predicted)]` field on a behaviour whose declared sync mode replicates no
  variables at all (`BehaviourSyncMode.None`/`NoVariableSync`), so no owner value can ever arrive and
  the prediction cannot converge. It does not examine `Manual` or `Continuous` behaviours, and CE0033
  normally rejects the case it does cover during codegen, so it is a backstop.

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
