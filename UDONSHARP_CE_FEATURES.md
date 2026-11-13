# UdonSharp CE - Feature Reference

Version: 1.2
Last Updated: 2026-11-11

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
| CE.Core | Implemented | CECallback is active; the optimization attributes are obsolete (using one is a compile error). |
| CE.Async | Partial | Delay/Yield and same-class child awaits work; result-awaiting is not wired. |
| CE.Data | Implemented | CEList, CEDictionary, and manual data bridge converters. |
| CE.Net | Partial | `[Sync]` uses the upstream Udon synced-variable pipeline; `[SyncOnJoin]` emits late-join glue for supported data graphs. |
| CE.Persistence | Partial | Generated per-model converters, validation, and PlayerData-backed Save/Restore are wired; Vector, Color and enum fields need a raw DataDictionary model, and schema migration is manual. |
| CE.DevTools | Implemented | Runtime logging, console, profiler, and monitor utilities. |
| CE.Perf | Implemented | ECS-lite world, spatial grid and LOD from Udon; `CEPool` and `BatchProcessor` take delegates, so they work in editor/plain C# only. |
| CE.Procgen | Implemented | Deterministic RNG, noise, dungeon, and WFC tools, all compilable to Udon. |
| CE.GraphBridge | Implemented | Attributes and editor generators for graph nodes. |
| CE.NetPhysics | Implemented (beta) | Predictive physics with input streaming and snapshots. |
| Editor Tools | Implemented | Analyzers, code generators, inspector, and windows. The six source-level optimizers and the assembly-level optimizer are present but disabled. |

## CE.Core

Key APIs:

- CECallback: parameterless callback abstraction (`Target`, `MethodName`, `Invoke`). `MethodName` has to be a name U# exports: a public parameterless method under its own name, or a built-in Udon event under its Udon name (`Interact` is `_interact`).
- CEPreserveAction: opt out of Action-to-CECallback transformation.
- Optimization attributes: CENoOptimize, CENoInline, CEInline, CENoUnroll, CEUnroll, CEConst, CEDebugOnly (obsolete with error; remove them from behaviour code).

Limitations:

- Only parameterless `System.Action` is transformed. `() => Method()`, `() => { Method(); }`, `delegate { Method(); }` and `new Action(Method)` become a CECallback when `Method` is a parameterless method of the same behaviour; a built-in event is emitted under its Udon name. `Action<T>`, `Func<T>`, closures and method groups assigned to an Action are not supported.
- An Action can be stored, passed, returned and called with `.Invoke()`. Any other use (`?.Invoke()`, `+=`, a null check, a conversion, an API that is not U# source) keeps the file as written, together with every file it shares an Action member with. CE0062 rejects a callback whose target SendCustomEvent cannot reach.
- Lambda inlining covers statement-level `List<T>.ForEach` and `CEList<T>.ForEach`, `ForEachWithIndex` and `ForEachUntil` only; any other lambda is reported by CE0021.
- Only CEPreserveAction is enforced by the compiler pipeline today.

## CE.Async

Key APIs:

- `async UdonTask` methods on an UdonSharpBehaviour, lowered at compile time into state machines that resume
  with SendCustomEventDelayedSeconds/Frames.
- UdonTask with Delay, DelayFrames and Yield; WhenAll and WhenAny combine statuses that are already final.
  UdonTask<T> holds a value you already have (FromResult).
- CancellationToken and CancellationTokenSource for cooperative cancellation.

Limitations:

- The transformer supports await on Delay, DelayFrames, Yield, and same-class async UdonTask child methods,
  as a statement of its own: in the method body, in a method-body-level while loop (one await per loop body;
  break and continue work), or in an early exit `if (...) { await ...; return; }`. Any other await is CE0024
  or CE0025 at that await.
- An awaited child's arguments must all be written out: an omitted optional argument or loose params
  elements is CE0025. Static, generic, overloaded and ref/out async methods are CE0025 as well.
- Syntax UdonSharp refuses in every method (local functions, try/throw, goto, tuples, `is`, `?.`, switch
  expressions and the rest) is CE0025 at the author's line inside an async method, where UdonSharp's own
  error would otherwise point into the generated state machine.
- One call per async method per behaviour can be in flight; a second start is refused and logged.
- Awaiting tasks that return values is not implemented: `async UdonTask<T>` is CE0022.
- WhenAll and WhenAny are not awaitable (CE0025). A UdonTask never updates, so they only report statuses
  that were already final.

## CE.Data

Key APIs:

- CEList<T>: array-backed list with DataList and JSON helpers.
- CEDictionary<TKey, TValue>: dictionary with DataDictionary and JSON helpers.
- CEDataBridge: manual converters between C# models and DataDictionary/JSON.
- DataModel, DataField, DataIgnore attributes (manual registration required).

Limitations:

- Default DataToken conversions only cover primitives, string, DataList, DataDictionary.
- CEList<T> requires T : IComparable under Udon compilation.
- JSON numbers deserialize as doubles. FromJson, FromDataList and FromDataDictionary convert any numeric token to the requested element type (integers truncate toward zero and clamp to range), so no cast is needed on those paths; a hand-written converter still sees the raw Double.
- CEDataBridge and every overload that takes a Func converter store or call delegates, which Udon cannot represent: they are usable from editor and plain C# code, not from an UdonSharpBehaviour. CEList's ForEach, ForEachWithIndex and ForEachUntil are the exception, because CE inlines the lambda.
- CEList's indexer, Insert and RemoveAt, and CEDictionary's indexer getter, treat a bad index or missing key as fatal: under Udon the calling behaviour halts.

## CE.Net

Key APIs:

- Sync, SyncOnJoin, SyncSerializer attributes.
- Authority enum (`Owner`, `Predicted`) — the required first argument to `[Sync]`.
- Rpc, RpcOwnerOnly, LocalOnly attributes.
- RateLimiter, NetworkLimits, ConflictResolver, MergeStrategyAttribute.

Behaviour sync mode is **inferred**. A behaviour that declares synced state and no
`[UdonBehaviourSyncMode]` gets one injected at compile time — Manual when something requires it
(synced arrays, `[SyncOnJoin]` payloads), Manual too when it syncs a `string` and interpolates
nothing, Continuous otherwise. The inference reads the behaviour's whole synced state: every partial
part of the class and every base class, so a derived behaviour that inherits an array or
`[SyncOnJoin]` state is Manual whatever its own fields are, and a partial behaviour gets exactly one
injected attribute. A mode you declared yourself is never silently overridden; a conflicting one is a
compile error (CE0033 for `None`/`NoVariableSync` with synced state, CE0034 for `Continuous` with
Manual-only state).

A synced `string` is a preference for Manual, not a requirement. Continuous does sync a string — it
only shares the ~200 byte budget with the rest of the behaviour — so `Continuous` plus a string you
declared yourself compiles, and a behaviour that interpolates one field and syncs a string beside it
is inferred Continuous, the only mode the interpolation is legal under. "Manual-only state" for
CE0034 means arrays and `[SyncOnJoin]`.

`[SyncOnJoin]` transports state as an `[UdonSynced]` string on the behaviour itself, written in
`OnPreSerialization` and applied in `OnDeserialization`. Authority is normal object ownership and
VRChat delivers the value to late joiners automatically. There is no coordinator object. `Group` gives
a behaviour one such string per group instead of one in total.

The payload is applied in `Start` too, ahead of the author's own `Start` body: VRChat drops an
`OnDeserialization` that arrives before a behaviour has started, which is the usual case for an
object that is inactive when a player joins. An empty or already-applied payload is skipped
silently. A serialization VRChat does not deliver is not retried — capture asks again only once some
payload changes — and the glue logs an error when one group's payload alone is over
`NetworkLimits.ManualSyncMaxBytes`.

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
- `Quantize` rounds the field itself, on the owner, each time the behaviour serializes — the owner's value snaps to the grid as well, and a change smaller than half a step between two serializations never accumulates. Keep a precise value in an unsynced field and assign the synced one from it when that matters. `DeltaEncode` / `DeltaEncodeHint` is advisory only; VRChat does not expose true delta sync for Udon fields.
- `[SyncOnJoin]` emits JSON glue for `bool`, the integer types, `float` and `double` (not `char` or `decimal`), enums, strings, supported Unity value structs, `DataDictionary`/`DataList`, arrays, `List<T>`, and Unity-serializable pure data models. An enum from UnityEngine, VRChat or System has to be `int`-backed with no negative value and none of 2048 or more. Scene/object references and cyclic or polymorphic graphs require `[SyncSerializer]`; CE0032 warns when generated glue is omitted, `static` and `const` fields included.
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
  Parameters must be Udon-serializable, at most 8, non-optional; the method must be a non-static method
  returning `void`. An unset `RateLimit` is `[NetworkCallable]`'s default of 5 events/second, never
  unlimited — the SDK rejects a network-callable method with a rate below 1. A `[NetworkCallable(n)]` written
  by hand is left as it is and `n` is the rate in effect; a `RateLimit` beside it that differs is reported
  (CE0011) and otherwise ignored. A rewritten call converts
  each argument to the declared parameter type (`SetVolume(1)` sends `1f` to a `float` parameter), because
  VRChat refuses an event whose argument types do not match. The attribute is recognised through its bound
  type, so an alias or an attribute class deriving from `RpcAttribute` is an `[Rpc]` too.
- `[LocalOnly]` is checked at compile time only (CE0012): a behaviour sending a network event to one of its
  own `[LocalOnly]` methods through `SendCustomNetworkEvent` or `CENet.Rpc`. It is not a runtime barrier, and
  a send to a different behaviour type is not reported.
- `RateLimiter` enforces a minimum interval of 1 / rate seconds between calls, not a count per second.
- `SyncOnJoin.Group` splits a behaviour's late-join state across payloads: fields sharing a group share one
  `[UdonSynced]` string with its own capture, restore and applied-guard, and fields naming no group share the
  default payload, which keeps the names it has always had. Capture requests serialization only when some group's JSON changes. Serialization remains per behaviour;
  independent group guards do not guarantee omission of unchanged payload strings from a packet. Grouping needs no second GameObject, which splitting across behaviours did — VRChat requires
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
- PersistenceLifecycle result records (`GetPendingSaveResult`, `GetPendingRestoreResult`, `GetPendingModelKey`, `GetPendingCorruptionInfo`) and PlayerObjectHelper slot mapping (local to each client; slots differ between clients, so they must not index synced arrays).
- Quota queries: `GetQuotaLimit()`, `GetUsedQuota()`, `GetRemainingQuota()`, `GetWritableQuota(key)`.

Limitations:

- `[PlayerData]` is the only registration path under Udon. `CEPersistence.Register<T>()` and the other generic entry points are editor-only and are marked `[Obsolete(error: true)]` for the Udon compile, so calling them from behaviour code fails the build naming the generated overload to use instead.
- The cross-behaviour `PersistenceLifecycle` callback registry (`RegisterCallback`, `IPersistenceCallbacks`) was removed: UdonSharp emits the statics of a non-behaviour type into each calling behaviour's own program heap, so a callback registered in behaviour A was invisible to a save issued from behaviour B. Read the `SaveResult`/`RestoreResult` returned in the calling behaviour, take corruption detail from `GetPendingCorruptionInfo()` there, and forward the outcome to other behaviours with `SendCustomEvent`.
- Save serializes the model `DataDictionary` as JSON into VRChat PlayerData with `PlayerData.SetString`; Restore reads it with `PlayerData.TryGetString`.
- Restore returns `NotReady` when the local player is unavailable. Wait for local `OnPlayerRestored` before interpreting `NoData` as a missing save.
- Auto-registration supports primitives, strings, DataList/DataDictionary, and arrays of those types; unsupported fields require explicit conversion inside a raw DataDictionary model.
- A `[PlayerData]` model must be a public or internal, non-abstract, non-static class with a public or internal parameterless constructor; an internal model gets internal overloads. Duplicate model or field keys, reserved `__ce_*` keys, inherited `[PersistKey]` fields, and attribute arguments CE cannot evaluate fail the compile at the declaration instead of generating code that does not compile or silently drops a constraint.
- Quota figures come from `Networking.GetPlayerDataStorageLimit`/`GetPlayerDataStorageUsage`, so they are account-wide totals, not per-key. `GetWritableQuota(key)` is the budget Save enforces against: remaining quota plus an estimate of the bytes that key already holds, since the write replaces them. Both APIs need a running client — they fall back to the 100KB constant and `-1` in edit mode — and ClientSim's usage number is approximate and logs a warning on every query, so cache it rather than polling per frame.

## CE.DevTools

Key APIs:

- CELogger with log levels and tags.
- CEDebugConsole for in-world log display.
- CEProfiler for timing and section profiling.
- PerformanceMonitor for lightweight FPS monitoring.

Limitations:

- A CELogger call with a constant level is rewritten at compile time to the matching `Debug.Log`,
  `Debug.LogWarning` or `Debug.LogError`, keeping its tag and severity but bypassing `MinLevel` and the
  in-world console. A call with named arguments or a non-constant level is left as a real CELogger call,
  filtered by the calling behaviour's own `MinLevel`.
- CELogger state (`MinLevel`, registered consoles) is static, and statics are per UdonBehaviour under
  Udon: settings made in one behaviour do not reach another, and a CEDebugConsole only receives logs
  written from its own behaviour. Call the console's `AddLog` to show logs from elsewhere.

## CE.Perf

Key APIs:

- CEWorld ECS-lite container, CEQuery, and ComponentTypeId.
- CEPool<T> for object pooling with O(1) handles.
- CEGrid for spatial partitioning.
- CELod for distance-based update frequency.
- BatchProcessor for time-sliced processing.

Limitations:

- CEComponent and CESystem attributes are metadata only; CEWorld does not auto-register them yet.
- CEPool<T> and BatchProcessor are built on delegates (factory, acquire/release callbacks, per-item actions), as are CEQuery.ForEach/ForEachWhile/ForEachCached and CEGrid.ForEachNonEmptyCell. Udon cannot represent a delegate and CE inlines lambdas only for List<T>.ForEach and the CEList<T> ForEach helpers, so these are usable from editor and plain C# code, not from an UdonSharpBehaviour. CEWorld, CELod, and the rest of CEQuery and CEGrid involve no delegates.
- A CEWorld holds at most 32 registered components (one 32-bit mask per entity). ComponentTypeId.MaxTypes (256) bounds the type id space, not the slot count.
- DestroyEntity, DestroyEntityDeferred and DisableEntity all swap-remove from ActiveEntities immediately; DestroyEntityDeferred defers only id recycling and mask clearing. A loop over ActiveEntities that removes entities must run backwards, or over a copy of the ids with an IsValidEntity check per id. For the snapshot form, defer destruction and do not flush/recycle ids until the pass ends.
- Destroying an entity clears its component mask, not the values in the registered arrays; a recycled id sees the previous entity's data until it is overwritten.

## CE.Procgen

Key APIs:

- CERandom: deterministic RNG with shuffle and weighted choice.
- CENoise: Perlin, Simplex, Worley, and fractal variants.
- CEDungeon: layout generation with rooms and corridors.
- WFCSolver: wave function collapse solver with incremental Tick.

Limitations:

- `CENoise.Initialize(seed)` seeds the calling behaviour only. UdonSharp gives every behaviour its own copy of a non-behaviour type's statics, so each behaviour that samples noise must call it; one that does not samples the seed-0 tables.
- `new CERandom()` is seeded from the clock and differs between clients; synchronized content needs `new CERandom(seed)` with a shared seed.
- WFCSolver has no backtracking: a contradiction ends the solve as `Failed`, and `Reset()` replays the same seed. Its grid is a flat array indexed `x * Height + y`.
- `CEDungeon.Generate` runs to completion in a single call, so a large layout costs a visible frame.

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
- FrameHistory, RollbackManager, StateCompressor, SyncPrioritizer.

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

Correction is not rollback in the replay sense: Udon cannot step PhysX, so `RollbackManager` measures
the difference between the authority's state and this client's recorded prediction for the same
frame (for the car you drive, the frame of the newest input the authority had applied) and applies
that difference to the present, easing small corrections in. Positions travel quantized inside
`NetPhysicsWorld.StateBoundsMin/Max`, a box measured from the world object.

Limitations:

- State packets default to a 32-entity cap and are further limited by payload size and the configured bandwidth budget.
- NetVehicle expects a Rigidbody and uses physics forces for motion. Throttle, brake and boost are forces in newtons, so tuning depends on the body's mass.
- A position outside `StateBoundsMin/Max` is clamped onto the box on every client but the authority.
- Entities left without an explicit `_entityId` are numbered by their place in the scene hierarchy, so ids agree across clients only while every client has the same entities in the same places: two entities on one GameObject, or an entity reparented or reordered at runtime, need an explicit id.

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
- CE0030: PlayerData size estimation.
- CE0063: `[Sync(Authority.Predicted)]` field on a behaviour whose declared sync mode replicates no
  variables at all (`BehaviourSyncMode.None`/`NoVariableSync`), so no owner value can ever arrive and
  the prediction cannot converge. It does not examine `Manual` or `Continuous` behaviours, and CE0033
  normally rejects the case it does cover during codegen, so it is a backstop.

Code generators (run on every compile; a failure is a compile error):

- Async method transformer.
- ForEach lambda inlining.
- Action to CECallback transformer.
- CELogger call transform.
- Sync-mode inference, `[SyncOnJoin]` glue, `[Rpc]` emitter, `[Sync(Quantize)]` emitter.
- Persistence auto-registration.

Optimizers (all disabled; none runs):

- Loop invariant code motion, loop unrolling, CSE, tiny-method inlining, extern call caching and string
  interning are registered but switched off, because each was found able to change program behaviour.
- The assembly-level optimizer under `Editor/Compiler/Optimization` is not wired into the compiler, and
  `UdonSharpCompileOptions.EnableAssemblyOptimization` is reserved with no effect.

Inspector and windows:

- CE Inspector with property grouping and optimization info.
- Bandwidth Analyzer, World Validator, Network Simulator, Late-Join Simulator.

## UdonSharp Subset Reminders

- Avoid named arguments and reflection.
- Initialize synced arrays at declaration time.
- Prefer simple data structures and arrays over complex generic or struct-heavy patterns.

NetPhysics compares historical state and corrects the present; it does not replay PhysX. Configure StateBoundsMin/Max relative to the world transform. WFC grids use flat indexing x * Height + y; dungeon distance matrices use from * roomCount + to.
