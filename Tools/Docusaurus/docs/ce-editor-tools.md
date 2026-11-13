# CE Editor Tools

CE Editor tools provide compile-time analyzers, code generators, a custom inspector, and diagnostics windows to catch issues early.

## Quick Start

- Open the optimization report: `Udon CE/Optimization Report`
- Analyze bandwidth: `Udon CE/Dev Tools/Bandwidth Analyzer`
- Validate a scene: `Udon CE/Dev Tools/World Validator`

## Compile-Time Analyzers

| ID | Area | Description |
| --- | --- | --- |
| CE0001 | Sync | Error on a synced array with no initializer, or one initialized to `null`: Udon cannot serialize a null synced array, so the behaviour's variable sync fails while it is null. The length you give is only a starting value — under Manual sync the array may be replaced by one of another length. The suggested fix keeps the field's own type and accessibility. |
| CE0002 | Performance | Warn on `GetComponent` in Update/FixedUpdate/LateUpdate. A lazy cache fill — `if (_renderer == null) _renderer = GetComponent<Renderer>();` on a field — is not reported; a null-checked local is, because it is empty again every frame. |
| CE0003 | Sync | Warn when a Continuous behaviour's fixed-size synced fields add up to more than ~200 bytes (each type at its own width: `bool` 1, `double` 8, `Quaternion` 16). Arrays and strings never reach this check: CE0034 rejects them under Continuous during codegen. |
| CE0010 | Net | Warn when a `[Sync]` tuning option is inert: `DeltaEncodeHint` on a non-array, `Quantize` on a non-float type, or interpolation under Manual sync. Does **not** check authority — see CE0014 and CE0063. |
| CE0011 | Net | Validate `[Rpc]` methods (parameters, sync mode, rate limits) and `[MergeStrategy]` placement. |
| CE0012 | Net | Error on network calls to `[LocalOnly]` methods. |
| CE0013 | Net | Warn that `[Sync(..., DeltaEncodeHint = true)]` is advisory only. VRChat has no delta sync for Udon synced variables; the whole field payload still goes on the wire. |
| CE0014 | Sync | Warn on a synced field written with no ownership check. Authority-aware: strict for `[Sync(Authority.Owner)]`, inferred for bare `[UdonSynced]`, and skipped entirely for `[Sync(Authority.Predicted)]`. Every way into the write has to be covered — an ownership check on the path, or an entry point where the write is expected (`Start`, the serialization and ownership callbacks, an `[Rpc(Target = RpcTarget.Owner)]`, or a handler this behaviour only ever sends with `SendCustomNetworkEvent(NetworkEventTarget.Owner, …)`). One covered caller no longer excuses an uncovered one. Handlers reached through `SendCustomEvent` and its delayed forms count their senders as callers; a delayed dispatch from `Start` does not inherit `Start`'s exemption. Writes to a member of a synced struct (`_target.y += 1f`) are writes to the field. |
| CE0015 | Sync | Warn when a Manual-sync behaviour writes synced fields but nothing in it ever calls `RequestSerialization()`, so the owner's writes never leave the owner's client. |
| CE0016 | Sync | Manual sync with nothing to serialize. Warns when `RequestSerialization()` is called on a behaviour with no synced fields of its own or in any base class (an empty payload costs a network message and replicates nothing); informational when there is no such call either. |
| CE0017 | Sync | Warn when `RequestSerialization()` runs from a per-frame event with no rate gate in front of it. A gate is a clock comparison, a comparison of two values, a `bool` dirty flag that the per-frame path or `OnPreSerialization`/`OnPostSerialization` assigns, or an edge-triggered input (`Input.GetKeyDown` and friends), and it may stand in a caller of the method that serializes. A comparison against `null`/`true`/`false`/`default`, or an ownership comparison, is not a gate: it holds for seconds at a time and every frame it lets through still serializes. |
| CE0018 | Net | Error on `SendCustomNetworkEvent` in a behaviour declaring `BehaviourSyncMode.None`, which removes it from networking entirely so the event is never sent. `NoVariableSync` keeps events and is fine. |
| CE0021 | Language | Error on lambda and anonymous-method usage that survives the CE optimizers and cannot be lowered to Udon. |
| CE0030 | Persistence | Warn when a `[PlayerData]` model's worst-case JSON size passes 80% of the 102,400-byte quota. Every model in the compilation is checked, whether or not a behaviour uses it. Numbers are counted at their longest spelling; arrays at their `[MaxLength]` or declared length (constants resolved); strings at their `[MaxLength]`. A field whose length cannot be read is left out of the figure and named in the report. Never an error: it is an estimate. |
| CE0032 | Sync | Warn when a `[SyncOnJoin]` field's type cannot be handled by generated late-join glue and needs `[SyncSerializer]`; the field is otherwise omitted from the payload. |
| CE0063 | Net | Warn when a `[Sync(Authority.Predicted)]` field sits on a behaviour whose declared sync mode replicates no variables at all (`None` or `NoVariableSync`), so the prediction has nothing it could converge on. It does not try to prove that a Manual behaviour publishes — see below for why. |

That table is every analyzer, not every code. Sixteen analyzers are registered in `CEAnalyzerRunner` and
each one has a row here; `GetAnalyzerInfo()` reports the same sixteen at runtime, so the table and the
runner can be compared directly. Codes beyond the sixteen row IDs come from two places:

- **Extra codes raised by an analyzer**, so a distinct failure stays searchable on its own. CE0011 raises CE0046 for a
  `[MergeStrategy]` that can never run, CE0047 for a by-reference `[Rpc]` parameter, and CE0050 for an
  `[Rpc]` whose argument count is not fixed.
- **Codes raised during codegen**, before or instead of analysis: CE0033/CE0034 from the
  inferred-sync-mode emitter, CE0011 (a `static` `[Rpc]`, or one with more than eight parameters) and
  CE0043/CE0045/CE0047–CE0050 from the `[Rpc]` lowering pass, CE0060 from the quantization emitter,
  CE0062 from the callback transformer, and CE0022–CE0026 from the [async transformer](ce-async). A
  generator failure that has no CE code of its own is reported under the generator's id (`CEGEN…`,
  `CEPERSIST001`).

CE0047 and CE0050 appear in both lists on purpose. The analyzer and the emitter report the same failure
under the same code, so a by-ref parameter reads identically whether it is caught before lowering or
during it.

One code belongs to no analyzer. **CE0019** is emitted when an analyzer itself throws: the compile
continues with a warning naming the analyzer that died, because the alternative — that analyzer's checks
quietly not running — is indistinguishable from a clean behaviour. A CE0019 means the codes owned by that
analyzer say nothing about this build.

Declaring `[Sync(Authority.Predicted)]` switches CE0014 off for that field, and **nothing takes its
place**. `Authority` records who may write the field — every client, with the owner's copy authoritative —
not where the local value came from: `NetVehicle._ownerPlayerId` is predicted and is never simulated, every
client latches it out of an `AssignDriver` broadcast. Declaring Predicted is therefore a *claim* that the
owner republishes and pulls the copies back together, and on a `Manual` or `Continuous` behaviour that
claim is taken on trust.

**CE0063 is a narrow backstop, not a replacement for the check that was switched off**, and the gap is
deliberate rather than unfinished. It reports when the behaviour's declared sync mode transmits no
variable to anyone, ever; it does **not** report a Manual behaviour that fails to serialize from the
owner. This whole feature exists because CE0014 was reporting five pieces of correct code, so an analyzer
that invents its own false positives moves the noise instead of removing it. Failing to *find* an
owner-reachable `RequestSerialization()` is not evidence that there is none: a manager behaviour can call
`other.RequestSerialization()`, an inspector-wired `SendCustomEvent` is invisible to source analysis, and
an ungated `RequestSerialization()` in `Update` does run on the owner every frame — that is CE0017's
bandwidth finding, not a divergence. So a predicted field on a Manual behaviour that genuinely never
publishes is left to review.

CE0015 covers the common form of that gap from the other side: it reports a Manual-sync behaviour that
writes synced fields with no `RequestSerialization()` anywhere in its own source, at the write, and its
fix text names the cross-behaviour call that would explain the absence.

Bare `[UdonSynced]` is unaffected by any of this: it keeps the inference CE0014 has always used, and
CE0063 only ever looks at fields that explicitly declared an authority.

In a clean compile CE0063 usually reports nothing, and that is expected — CE0033 rejects
`None`/`NoVariableSync` alongside synced state during codegen, before analysis runs at all. CE0063 is the
backstop, so that narrowing CE0033 later cannot quietly turn "cannot converge" into "nobody looks".

## Compile-Time Code Generators

These run on every compile, in this order, before UdonSharp binds the program. They are not
optimizations — the program is incorrect without their output — so none can be switched off, and a
failure in one is a compile error rather than a warning.

| Generator | Purpose |
| --- | --- |
| Async Method Transformer | Rewrites `async UdonTask` methods into state machines. |
| Lambda Inlining | Inlines `ForEach`-style lambdas on supported collections into loops. |
| Action To CECallback | Converts parameterless `Action` to `CECallback`. |
| CELogger Transform | Rewrites `CELogger` calls into `Debug.Log` calls. |
| Sync Mode Inference | Injects `[UdonBehaviourSyncMode]` on behaviours with synced state and no declared mode. |
| SyncOnJoin Glue | Emits the serialize/deserialize pair and synced payload for `[SyncOnJoin]` fields. |
| Rpc Emitter | Marks `[Rpc]` methods `[NetworkCallable]` and rewrites their call sites into sends. |
| Quantize Emitter | Rounds `[Sync(Quantize = …)]` fields in `OnPreSerialization`. |
| Persistence Auto-Registration | Generates `CEPersistence` converters from `[PlayerData]` models. |

## Compile-Time Optimizers

All six are **disabled** in this release and do not run. Each was found able to change program
behaviour, so they stay off until rewritten against the semantic model. They remain listed in the
Optimization Report window as disabled.

| Optimizer | What it would do |
| --- | --- |
| Loop Invariant Code Motion | Hoist loop-invariant expressions. |
| Small Loop Unrolling | Unroll tiny fixed loops. |
| Common Subexpression Elimination | Reuse repeated expressions. |
| Tiny Method Inlining | Inline small private methods. |
| Extern Call Caching | Cache repeated extern property access. |
| String Interning | Deduplicate string literals. |

The assembly-level optimizer under `Editor/Compiler/Optimization` is likewise not wired into the
compiler; `UdonSharpCompileOptions.EnableAssemblyOptimization` is reserved and has no effect.

## CE Inspector

The CE Inspector is an optional inspector for UdonSharpBehaviours. It is switched on by UdonSharp's own
project setting: **Project Settings > Udon Sharp > Default Behaviour Editor**, set to **CE Inspector**.
It adds:

- Property grouping and quick status badges.
- A sync status bar and optimization summaries from the last compile.

It keeps UdonSharp's header (sync, interaction and utility settings) and edits several selected
objects at once; the status bar and the Advanced section appear for a single selection only. It does not
include UdonSharp's fallback editor for `[OdinSerialize]` jagged arrays, so those fields cannot be edited
while it is selected. A `[CustomEditor]` you write for your own behaviour type still takes precedence.

Preferences under `Edit > Preferences > UdonSharp CE > Inspector` set optimization info and property
grouping. The "Use CE Inspector" box there only shows the UdonSharp setting above and links to it.

Menu shortcuts:

- `Udon CE/Inspector/Toggle Optimization Info`
- `Udon CE/Inspector/Toggle Property Grouping`
- `Udon CE/Inspector/Preferences`

## Dev Tools Windows

| Menu Path | Purpose |
| --- | --- |
| `Udon CE/Dev Tools/Bandwidth Analyzer` | Estimate sync payload and bandwidth usage. |
| `Udon CE/Dev Tools/World Validator` | Run scene-wide checks and recommendations. |
| `Udon CE/Dev Tools/Network Simulator` | Model loss, latency, jitter, duplication and reordering for packets passed to `NetworkSimulator.SimulatePacket` (the window's Test Packet button sends one). It does not sit between your world and VRChat or ClientSim: synced variables and network events are unaffected, and the bandwidth limit only feeds an estimate. Runs from its own seeded random generator, never `UnityEngine.Random`. |
| `Udon CE/Dev Tools/Late-Join Simulator` | In play mode, capture every running behaviour's synced variables, then write them back and raise `OnDeserialization` on each UdonBehaviour — what VRChat hands a player who joins — and report which behaviours kept that state or halted. It does not reset non-synced state the way a fresh joiner starts, and it does nothing outside play mode. |

## Limitations

- The optimization attributes (`[CEInline]`, `[CENoInline]`, `[CENoOptimize]`, `[CEUnroll]`, `[CENoUnroll]`, `[CEConst]`, `[CEDebugOnly]`) are `[Obsolete(error: true)]`: using one is a compile error, because the optimizers they addressed are disabled and an attribute that silently does nothing is worse than none. Remove them. `[CEPreserveAction]` is the one attribute in that file that is still read.
- Async transformation supports `Delay`, `DelayFrames`, `Yield`, and same-class async UdonTask child methods. Awaiting task results is not supported.
- `CELogger` calls are transformed to `Debug.Log` for Udon compatibility; filtering is bypassed.

## Common Pitfalls

### Bad

```csharp
[CEInline]
private float Square(float x) => x * x; // Compile error: CEInline is obsolete and does nothing
```

### Good

```csharp
private float Square(float x) => x * x; // No attribute. Nothing inlines it; write it inline yourself if the call cost matters
```
