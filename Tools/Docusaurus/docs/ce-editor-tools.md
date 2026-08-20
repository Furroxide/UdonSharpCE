# CE Editor Tools

CE Editor tools provide compile-time analyzers, code optimizers, a custom inspector, and diagnostics windows to catch issues early.

## Quick Start

- Open the optimization report: `Udon CE/Optimization Report`
- Analyze bandwidth: `Udon CE/Dev Tools/Bandwidth Analyzer`
- Validate a scene: `Udon CE/Dev Tools/World Validator`

## Compile-Time Analyzers

| ID | Area | Description |
| --- | --- | --- |
| CE0001 | Sync | Error on uninitialized synced arrays. |
| CE0002 | Performance | Warn on `GetComponent` in Update/FixedUpdate/LateUpdate. |
| CE0003 | Sync | Warn when continuous sync payload exceeds ~200 bytes. |
| CE0010 | Net | Warn when a `[Sync]` tuning option is inert: `DeltaEncodeHint` on a non-array, `Quantize` on a non-float type, or interpolation under Manual sync. Does **not** check authority — see CE0014 and CE0063. |
| CE0011 | Net | Validate `[Rpc]` methods (parameters, sync mode, rate limits) and `[MergeStrategy]` placement. |
| CE0012 | Net | Error on network calls to `[LocalOnly]` methods. |
| CE0013 | Net | Warn that `[Sync(..., DeltaEncodeHint = true)]` is advisory only. VRChat has no delta sync for Udon synced variables; the whole field payload still goes on the wire. |
| CE0014 | Sync | Warn on a synced field written with no ownership check. Authority-aware: strict for `[Sync(Authority.Owner)]`, inferred for bare `[UdonSynced]`, and skipped entirely for `[Sync(Authority.Predicted)]`. |
| CE0015 | Sync | Warn when a Manual-sync behaviour writes synced fields but nothing in it ever calls `RequestSerialization()`, so the owner's writes never leave the owner's client. |
| CE0016 | Sync | Manual sync with nothing to serialize. Warns when `RequestSerialization()` is called on a behaviour with no synced fields (an empty payload costs a network message and replicates nothing); informational when there is no such call either. |
| CE0017 | Sync | Warn when `RequestSerialization()` runs from a per-frame event with no rate gate in front of it — no time comparison, equality check, or dirty flag on the path to the call. |
| CE0018 | Net | Error on `SendCustomNetworkEvent` in a behaviour declaring `BehaviourSyncMode.None`, which removes it from networking entirely so the event is never sent. `NoVariableSync` keeps events and is fine. |
| CE0020 | Async | Validate `UdonTask` methods and unsupported patterns. |
| CE0021 | Language | Error on lambda and anonymous-method usage that survives the CE optimizers and cannot be lowered to Udon. |
| CE0030 | Persistence | Estimate PlayerData size and warn near 100KB quota. |
| CE0032 | Sync | Warn when a `[SyncOnJoin]` field's type cannot be handled by generated late-join glue and needs `[SyncSerializer]`; the field is otherwise omitted from the payload. |
| CE0063 | Net | Warn when a `[Sync(Authority.Predicted)]` field sits on a behaviour whose declared sync mode replicates no variables at all (`None` or `NoVariableSync`), so the prediction has nothing it could converge on. It does not try to prove that a Manual behaviour publishes — see below for why. |

That table is every analyzer, not every code. Seventeen analyzers are registered in `CEAnalyzerRunner` and
each one has a row here; `GetAnalyzerInfo()` reports the same seventeen at runtime, so the table and the
runner can be compared directly. Codes beyond the seventeen row IDs come from two places:

- **Extra codes raised by an analyzer**, so a distinct failure stays searchable on its own. CE0020 raises
  the async set CE0022–CE0026, documented on [CE Async](ce-async). CE0011 raises CE0046 for a
  `[MergeStrategy]` that can never run, CE0047 for a by-reference `[Rpc]` parameter, and CE0050 for an
  `[Rpc]` whose argument count is not fixed.
- **Codes raised during codegen**, before or instead of analysis: CE0033/CE0034 from the
  inferred-sync-mode emitter, CE0043/CE0045/CE0047–CE0050 from the `[Rpc]` lowering pass, CE0060–CE0062
  from the quantization and callback transformers.

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

## Compile-Time Optimizers

| Optimizer | Purpose |
| --- | --- |
| Async Method Transformer | Rewrites `async UdonTask` methods into state machines. |
| Action To CECallback | Converts parameterless `Action` to `CECallback`. |
| CELogger Transform | Rewrites `CELogger` calls into `Debug.Log` calls. |
| Loop Invariant Code Motion | Hoists loop-invariant expressions. |
| Small Loop Unrolling | Unrolls tiny fixed loops. |
| Common Subexpression Elimination | Reuses repeated expressions. |
| Tiny Method Inlining | Inlines small private methods. |
| Extern Call Caching | Caches repeated extern property access. |
| String Interning | Deduplicates string literals. |

## CE Inspector

The CE Inspector enhances UdonSharpBehaviour inspectors with:

- Property grouping and quick status badges.
- Optimization summaries from the last compile.
- Preferences under `Edit > Preferences > UdonSharp CE > Inspector`.

Menu shortcuts:

- `Udon CE/Inspector/Toggle Optimization Info`
- `Udon CE/Inspector/Toggle Property Grouping`
- `Udon CE/Inspector/Preferences`

## Dev Tools Windows

| Menu Path | Purpose |
| --- | --- |
| `Udon CE/Dev Tools/Bandwidth Analyzer` | Estimate sync payload and bandwidth usage. |
| `Udon CE/Dev Tools/World Validator` | Run scene-wide checks and recommendations. |
| `Udon CE/Dev Tools/Network Simulator` | Simulate lag, loss, and jitter. |
| `Udon CE/Dev Tools/Late-Join Simulator` | Simulate late-join sync behavior. |

## Limitations

- Optimization attributes such as `[CEInline]` and `[CENoOptimize]` are declared but not enforced yet.
- Async transformation supports `Delay`, `DelayFrames`, and `Yield` only. Awaiting task results is not supported.
- `CELogger` calls are transformed to `Debug.Log` for Udon compatibility; filtering is bypassed.

## Common Pitfalls

### Bad

```csharp
[CEInline]
private float Square(float x) => x * x; // Attribute is not enforced yet
```

### Good

```csharp
private float Square(float x) => x * x; // Let the optimizer decide
```
