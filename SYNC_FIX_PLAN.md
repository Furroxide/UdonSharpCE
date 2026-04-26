# UdonSharpCE: Fix the Sync Pipeline (A1–A5)

## Progress Status (2026-04-26)

This plan is no longer purely prospective. The core sync-pipeline work has been implemented, including the deep `[SyncOnJoin]` follow-up pass requested on 2026-04-26.

| Area                                    |                     Status | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| --------------------------------------- | -------------------------: | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A1 `[Sync]` upstream integration        |                       Done | `SyncAttribute` now inherits `UdonSyncedAttribute`, keeps interpolation mapped to `NetworkSyncType`, and related tooling uses assignable synced-attribute lookup. Existing upstream binder calls still read `GetAttribute<UdonSyncedAttribute>()`.                                                                                                                                                                                           |
| A2/A3 `[SyncOnJoin]` glue and transport |                       Done | `SyncOnJoinGlueEmitter` generates payload/inbound fields, serialize/deserialize events, and nested helpers for Unity-serializable pure data graphs. Supported roots include primitives, enums, strings, Udon-compatible Unity value structs, `DataDictionary`/`DataList`, one-dimensional arrays, `List<T>`, and custom classes/structs walked through Unity-serialized fields. Manual-only roots emit CE0032 as a warning and are omitted unless `[SyncSerializer(...)]` is supplied. |
| A4 Quantize / DeltaEncode honesty       |                       Done | `QuantizePreSerializeEmitter` injects `OnPreSerialization` rounding. `DeltaEncode` is now an obsolete compatibility alias over `DeltaEncodeHint`, and analyzers/tooling treat it as advisory.                                                                                                                                                                                                                                                |
| A5 storage cache thread-safety          |                       Done | `UdonVariableStorageInterface` now locks `_variableTypeLookup` for cache clear/build/read, preventing stale cache publish races.                                                                                                                                                                                                                                                                                                             |
| Persistence backing for showcase saves  |                       Done | `CEPersistence.Save/Restore` and generated overloads now serialize model `DataDictionary` JSON to VRChat `PlayerData.SetString` and restore with `PlayerData.TryGetString`, returning `NotReady`/`NoData` as appropriate.                                                                                                                                                                                                                    |
| Treasure hunt saved-data load timing    |                       Done | `InventorySystem` waits for `OnPlayerRestored(VRCPlayerApi player)` before reading PlayerData and skips auto-save until local PlayerData is restored.                                                                                                                                                                                                                                                                                        |
| Treasure collectible authority/sync     |                       Done | `CollectibleItem` defers collection until local ownership transfer completes, syncs collected state plus server timestamp, limits respawn serialization to the owner, and prevents remote deserialization from crediting local inventory.                                                                                                                                                                                                    |
| Cutscene async lowering                 | Done for showcase patterns | `AsyncMethodTransformOptimizer` now preserves awaited child calls via continuations, supports the showcase `while` + `await` patterns, and throws on unsupported await targets instead of silently dropping work.                                                                                                                                                                                                                            |
| Cutscene scene-reference fallback       |                       Done | `CutsceneController.Start()` now auto-wires missing safe child references at runtime when serialized scene fields are null/empty.                                                                                                                                                                                                                                                                                                            |

Verification completed after the deep `[SyncOnJoin]` pass:

- Focused EditMode fixtures: `23 passed, 0 failed` across `CEShowcase.Tests.Editor.UdonSharpCESyncPipelineTests` and `CEShowcase.Tests.Editor.Station5PersistenceRegressionTests`.
- Full UdonSharp compile: `Errors=0, Warnings=0` (`8` informational diagnostics).
- A first batchmode Unity test attempt could not run because this project was already open in the interactive Unity editor; verification was rerun through Unity MCP against the open editor.

Remaining work:

- Promote the current showcase/editor regression coverage into the originally planned package-level `Tests~/CE/...` layout if that test organization is still desired.
- Run live multi-client VRChat/ClientSim smoke coverage for late-join sync and treasure collection. The automated EditMode tests and full compiler pass are green, but live multi-user runtime behavior was not exercised in this session.
- Broader package-level tests were not run in this session.

## Context

UdonSharpCE is a fork of VRChat's UdonSharp C# compiler. The project markets itself on filling VRChat SDK gaps and "automagically" hiding complexity. An audit (three parallel Explore passes) surfaced ~15 fundamental flaws across sync, optimizers, and runtime libraries. This plan is scoped to the **sync pipeline**, with "wire real implementations" as the repair philosophy.

This plan addresses the five sync-pipeline flaws (A1–A5). The current situation is user-hostile: a developer writes `[Sync] public int score;` and `[SyncOnJoin] public GameState state;`, sees no compiler error, ships their world, and discovers in production that **nothing syncs and late joiners see empty state**. This is the worst kind of API failure — silent, confidence-eroding, and expensive to debug inside VRChat.

## Root-Cause Summary

| #   | Root cause                                                                                                                                                                                                                                                                                                                                                                                                                                                                          | File(s)                                                                                                                                                                                           |
| --- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A1  | CE's `[Sync]` attribute is invisible to the core binder. `FieldSymbol.SyncMode` only inspects `[UdonSynced]`, so the entire upstream sync pipeline (`CheckSyncCompatibility`, heap init, UASM emission) never sees CE-synced fields. They compile without error but produce no synced slot.                                                                                                                                                                                         | `Editor/Compiler/Binder/Symbols/FieldSymbol.cs:61-62`, `Editor/Compiler/UdonSharpCompilerV1.cs:913,1143`                                                                                          |
| A2  | `LateJoinerSync.SerializeBehaviour()` returns `behaviour.name`. The comment openly says "full implementation would use reflection on `[SyncOnJoin]` fields." **Reflection isn't available at Udon runtime**, so the stub can't be fixed in place — it requires compiler-emitted glue.                                                                                                                                                                                               | `Runtime/Libraries/CE/Net/LateJoinerSync.cs:454-459`                                                                                                                                              |
| A3  | Symmetric to A2: `DeserializeBehaviour()` is an empty method body. Late joiners cannot apply received state even if A2 were fixed.                                                                                                                                                                                                                                                                                                                                                  | `Runtime/Libraries/CE/Net/LateJoinerSync.cs:548-552`                                                                                                                                              |
| A4  | `SyncAttribute.Quantize` and `SyncAttribute.DeltaEncode` have docstrings promising bandwidth savings, but there is no transform and no hook into serialization. They are pure analyzer hints dressed as runtime features.                                                                                                                                                                                                                                                           | `Runtime/Libraries/CE/Net/SyncAttribute.cs:58-79`                                                                                                                                                 |
| A5  | `UdonVariableStorageInterface.ClearCache()` is called from `CleanupCompile` on the main thread (safe today), but the static cache is read by serializer code at arbitrary times. If compilation ever moves fully off-main (CE already runs optimizers on background threads), the unlocked `Dictionary` can corrupt. Also, there is no assertion that `ClearCache()` has run before `GetElementStorage()` is called post-recompile, so a stale program asset reference can survive. | `Editor/Serialization/StorageInterfaces/UdonVariableStorageInterface.cs:106-115,117-136`; callers at `Editor/Compiler/UdonSharpCompilerV1.cs:286-287`, `Editor/UdonSharpEditorUtility.cs:217-218` |

## Guiding Principle

**Honesty over optimism.** Every fix here either makes the API work as advertised, or surfaces a diagnostic instead of silently skipping work. Manual-only `[SyncOnJoin]` roots now warn with CE0032 and require `[SyncSerializer(...)]` for generated late-join glue.

---

## Fix Design

### A1 — Make `[Sync]` actually sync

**Approach (minimal blast radius):** make `UdonSharp.CE.Net.SyncAttribute` inherit from `UdonSharp.UdonSyncedAttribute`. Forward `SyncAttribute.SyncMode` (already derived from `InterpolationMode`) to the base constructor.
Because named attribute arguments execute after base construction, `SyncAttribute.Interpolation` must also keep the inherited `NetworkSyncType` property in sync; ctor-only wiring is not enough for `[Sync(Interpolation = InterpolationMode.Linear)]`.

This means:

- Zero change to the core binder/emitter — `GetAttribute<UdonSyncedAttribute>()` at `Packages/com.merlin.UdonSharp/Editor/Compiler/Binder/Symbols/FieldSymbol.cs:61`, `Packages/com.merlin.UdonSharp/Editor/Compiler/UdonSharpCompilerV1.cs:913`, and `Packages/com.merlin.UdonSharp/Editor/Compiler/UdonSharpCompilerV1.cs:1143` resolves `[Sync]` automatically via inheritance.
- `[Sync]` fields go through the full existing pipeline: type-compat check, heap slot, sync mode enforcement.
- The analyzer at `Packages/com.merlin.UdonSharp/Editor/CE/Net/SyncFieldAnalyzer.cs:119-127` becomes obsolete (no longer an "also has `[UdonSynced]`" info). Simplify it.

**Files to change:**

- `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net/SyncAttribute.cs` — change declaration to `public class SyncAttribute : UdonSyncedAttribute`, call `base(SyncMode)` from constructor. Drop the redundant `SyncMode` property (inherited as `NetworkSyncType`).
- `Packages/com.merlin.UdonSharp/Editor/CE/Net/SyncFieldAnalyzer.cs:119-127` — remove the "both present" info diagnostic; keep Quantize/DeltaEncode/Interpolation validations.
- `Packages/com.merlin.UdonSharp/Editor/CE/Net/BandwidthEstimator.cs:439,466` — already handles both names; keep.
- `Packages/com.merlin.UdonSharp/Editor/CE/DevTools/Analysis/BehaviourAnalyzer.cs`, `Packages/com.merlin.UdonSharp/Editor/CE/DevTools/Validation/Validators/SyncModeValidator.cs`, and `Packages/com.merlin.UdonSharp/Editor/CE/DevTools/Validation/Validators/UninitializedSyncedArrayValidator.cs` — detect synced fields via assignable attribute lookup, not literal `UdonSynced*` name checks, so `[Sync]` participates in tooling.

**Edge case:** users who write `[Sync][UdonSynced]` on the same field will now be applying the base attribute twice. `AttributeUsage(AllowMultiple = false)` on `UdonSyncedAttribute` means this throws a C# error — which is the right outcome (force one or the other). Document the breaking change in `UDONSHARP_CE_FEATURES.md`.

### A2/A3 — Make `[SyncOnJoin]` actually sync

**Approach:** emit per-behaviour serialize/deserialize glue at compile time. Reflection isn't viable in Udon runtime, so we lean on the compiler. Use VRChat's `VRCJson` + `DataDictionary` as the wire format — it's fully supported in Udon and already pulled in by CE.Data.

**New contract per-behaviour with `[SyncOnJoin]` fields:**

```csharp
// Emitted by the compiler (CE optimizer pass), not written by user:
public string _CE_SyncOnJoin_Serialize() {
    var dict = new DataDictionary();
    dict["phase"] = new DataToken(gamePhase);
    dict["completed"] = /* array → DataList */;
    VRCJson.TrySerializeToJson(new DataToken(dict), JsonExportType.Minify, out DataToken json);
    return json.String;
}
public void _CE_SyncOnJoin_Deserialize(string payload) {
    VRCJson.TryDeserializeFromJson(payload, out DataToken root);
    var dict = root.DataDictionary;
    if (dict.TryGetValue("phase", out DataToken v)) gamePhase = (int)v.Number;
    /* ... */
}
```

**LateJoinerSync changes:**

- Do not key payloads by `behaviour.name`. Use a stable registration identifier captured during `RegisterBehaviour()` — for example the transform sibling-index path plus the backing `UdonBehaviour` component slot — so duplicate object names and multiple UdonSharp behaviours on one GameObject still deserialize to the correct target.
- `SerializeBehaviour(behaviour)` at `LateJoinerSync.cs:454` → call `behaviour.SendCustomEvent("_CE_SyncOnJoin_Serialize")` then read `behaviour._CE_SyncOnJoinPayload` (a generated public string field that the emitted method writes to). Udon SendCustomEvent discards return values, so we must use a field-return idiom.
- `DeserializeBehaviour(behaviour, data)` at `LateJoinerSync.cs:548` → set `behaviour._CE_SyncOnJoinInbound = data;` then `SendCustomEvent("_CE_SyncOnJoin_Deserialize_Trigger")` which reads the inbound field and dispatches.

**Alternative if emitting behaviour members turns out invasive:** require users to override `public virtual string _CE_SyncOnJoinSerialize()` / `public virtual void _CE_SyncOnJoinDeserialize(string)` on their UdonSharpBehaviour, and CE generates the body via source generators only when the base is not overridden. Either way the user experience is identical (annotate `[SyncOnJoin]`, it just works).

**Files to create/modify:**

- NEW: `Packages/com.merlin.UdonSharp/Editor/CE/Optimizers/SyncOnJoinGlueEmitter.cs` — an `ICECompileTimeOptimizer` that visits `UdonSharpBehaviour` classes with at least one `[SyncOnJoin]` field and appends the two generated methods + the two payload fields.
- MODIFY: `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net/LateJoinerSync.cs:454-459,548-552` — call the generated methods via `SendCustomEvent`, read/write payload fields via `GetProgramVariable`/`SetProgramVariable` (see `UdonBehaviour` API for field access).
- MODIFY: `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net/SyncOnJoinAttribute.cs` — add `internal` marker `class SyncOnJoinGenerated : Attribute` that the emitter stamps on generated methods so the compiler ignores them if already present (idempotent re-emit).
- MODIFY: CE analyzer: add `CE0031` — "[SyncOnJoin] used but no LateJoinerSync component found in scene" (scene-level check, can live in `Editor/CE/DevTools/Validation/Validators/`).

**Type support:** emit JSON glue for primitives, enums, strings, supported Udon-compatible Unity value structs, `DataDictionary`/`DataList`, one-dimensional arrays, `List<T>`, and Unity-serializable pure data models. Custom models are walked through public instance fields and private `[SerializeField]` fields; static, readonly, `[NonSerialized]`, `[DataIgnore]`, and `[PersistIgnore]` fields are skipped. `DataField("key")` and `PersistKey("key")` provide nested field keys when present. Manual-only roots such as `UnityEngine.Object` references, delegates/events, interfaces, abstract/open generic types, `[SerializeReference]` polymorphic fields, non-`DataDictionary` dictionaries, jagged/multidimensional arrays, and recursive graphs emit CE0032 as a warning and are omitted from generated glue unless `[SyncSerializer(...)]` is supplied.

**Chunking and transport** are already correct in `LateJoinerSync` (master-driven, `[UdonSynced]` string chunks, `SendCustomEventDelayedSeconds` pacing, timeout). No transport changes required.

### A4 — Quantize / DeltaEncode honesty

**Approach:** implement Quantize properly (it's cheap, works with any `[UdonSynced]` float), mark DeltaEncode as hint-only (requires Manual sync + user cooperation for a correct implementation).

**Quantize:**

- Add a compiler pass: for any field with `[Sync(Quantize = Q)]`, inject a pre-serialize quantizer into the behaviour's `OnPreSerialization` override (create one if missing; chain to user's if present). For float: `field = Mathf.Round(field / Q) * Q;`. For Vector3: component-wise. Saves no bytes at the VRChat network layer (they don't expose sub-byte packing), but it **does** shrink the effective precision so downstream compression/delta-on-same-value skips become possible, and — more importantly — it matches the documented contract.
- The quantize and SyncOnJoin emitters must classify field types from Roslyn semantic symbols, not `TypeSyntax.ToString()`, so aliases like `using Vec = UnityEngine.Vector3;` still receive generated code.
- Files: NEW `Packages/com.merlin.UdonSharp/Editor/CE/Optimizers/QuantizePreSerializeEmitter.cs`; reference `SyncAttribute` from runtime.

**DeltaEncode:**

- Rename runtime property to `DeltaEncodeHint` with `[Obsolete("DeltaEncode is a bandwidth-estimator hint only; VRChat does not support true delta sync. Use Manual sync + a custom serializer for real delta encoding.", false)]` on the old name so existing code compiles with a warning.
- Update `SyncFieldAnalyzer.cs` to emit CE0013: "DeltaEncode is advisory; set `UdonBehaviourSyncMode(Manual)` and use `RequestSerialization()` after changing specific indices for real delta sync."
- Update `UDONSHARP_CE_FEATURES.md` to state this plainly.

### A5 — ClearCache thread-safety and ordering

**Approach:** low-risk hardening.

- Wrap `_variableTypeLookup` access in a private `lock (_sync)` in all three methods (`ClearCache`, `GetElementType`, constructor). Unity's editor is mostly single-threaded but CE optimizer passes already run on background threads (confirmed earlier in explore).
- Keep the field-definition snapshot and cache publish under that same lock (or add a generation check) so `ClearCache()` cannot invalidate the cache and then have stale definitions written back immediately afterward.
- Add a domain-reload assertion: `[InitializeOnLoadMethod]` that checks `_variableTypeLookup.Count == 0` at load and logs a warning if not (means a reference survived reload unexpectedly).

**Files:**

- MODIFY: `Packages/com.merlin.UdonSharp/Editor/Serialization/StorageInterfaces/UdonVariableStorageInterface.cs:106-136`.

---

## Critical Files

Existing — read before modifying:

- `Packages/com.merlin.UdonSharp/Editor/Compiler/Binder/Symbols/FieldSymbol.cs` (line 61: `SyncMode`)
- `Packages/com.merlin.UdonSharp/Editor/Compiler/UdonSharpCompilerV1.cs` (lines 286, 913, 1143; optimizer integration around 1164–1221)
- `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net/SyncAttribute.cs`
- `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net/SyncOnJoinAttribute.cs`
- `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net/LateJoinerSync.cs` (serialize/apply paths now call generated glue through Udon program variables)
- `Packages/com.merlin.UdonSharp/Editor/Serialization/StorageInterfaces/UdonVariableStorageInterface.cs`
- `Packages/com.merlin.UdonSharp/Editor/CE/Net/SyncFieldAnalyzer.cs`
- `Packages/com.merlin.UdonSharp/Editor/Compiler/CompileTimeOptimizerRegistry.cs` (pattern to mirror for new emitters)

New files to create:

- `Packages/com.merlin.UdonSharp/Editor/CE/Optimizers/SyncOnJoinGlueEmitter.cs`
- `Packages/com.merlin.UdonSharp/Editor/CE/Optimizers/QuantizePreSerializeEmitter.cs`
- `Packages/com.merlin.UdonSharp/Editor/CE/DevTools/Validation/Validators/SyncOnJoinSceneValidator.cs`
- Tests (see below)

Existing optimizer utilities to reuse:

- Look at `Editor/CE/Optimizers/AsyncMethodTransformOptimizer.cs` and `Editor/CE/Optimizers/TinyMethodInliningOptimizer.cs` for the `ICECompileTimeOptimizer` contract and syntax-tree rewriter patterns — both emitters should be straight `CSharpSyntaxRewriter` passes.

## Verification Plan

**Unit tests (`Packages/com.merlin.UdonSharp/Tests~/`):**

1. `Tests~/CE/Net/SyncAttributeIntegrationTests.cs`
   - Define behaviour with `[Sync(InterpolationMode.Linear)] Vector3 pos;`. Compile. Assert the resulting program has a synced heap slot with `UdonSyncMode.Linear`.
   - Define with `BehaviourSyncMode.Manual` + `[Sync(InterpolationMode.Linear)]` — assert compile error (existing check now fires for CE).
   - Define with invalid sync type (e.g. `[Sync] UnityEngine.Object o;`) — assert compile error.
2. `Tests~/CE/Net/SyncOnJoinEmitterTests.cs`
   - Compile behaviour with `[SyncOnJoin] int gamePhase; [SyncOnJoin] bool[] completed;`. Introspect the emitted syntax tree; assert `_CE_SyncOnJoin_Serialize` / `_CE_SyncOnJoin_Deserialize` exist and have correct bodies.
   - Round-trip: run Serialize → pass string → Deserialize on a fresh instance → assert field equality.
3. `Tests~/CE/Net/QuantizeEmitterTests.cs`
   - `[Sync(Quantize = 0.1f)] float value = 0.1234f;` → after `OnPreSerialization`, assert `value == 0.1f`.
4. `Tests~/CE/Serialization/UdonVariableStorageInterfaceThreadTests.cs`
   - Hammer `ClearCache()` and `GetElementType()` from parallel tasks; assert no exception and no deadlock.

**Integration tests (`Assets/Scripts/CEShowcase/Tests/`):** 5. `LateJoinerSyncClientSimTest.cs` — use ClientSim's multi-client to simulate: master sets state, second client joins, assert the joiner's fields match after `LateJoinerSyncStatus.Complete`.

**Manual smoke tests:** 6. Open `Assets/IntegrationTestScene.unity` (already in repo, untracked). Build and run in ClientSim with two simulated clients. Verify a `[Sync]` Vector3 interpolates, a `[SyncOnJoin]` game phase restores on late-join, and no warnings fire in the console.

**Regression guard:** 7. Run existing `Tests~/` suite end-to-end — sync-compatibility tests (if any) must still pass on plain `[UdonSynced]`, since the fix is purely additive.

---

## Out of Scope (explicitly deferred to later sessions)

- B1–B5 (optimizer safety): silent exception swallowing, unsafe caching, synced-field-aware inlining. Will cause issues independent of sync pipeline; flagged for next session.
- C1–C5 (runtime placeholders): `await` scheduling, `CEPersistence.Save/Restore`, `WhenAll/WhenAny`, pool recycle of synced fields.
- Tooling honesty updates (`UDONSHARP_CE_FEATURES.md` sweep, `[Obsolete]` annotations on unfixed placeholders).
- Full `BandwidthEstimator.cs` accuracy audit.

These are all real flaws, but splitting them into separate sessions keeps each change reviewable and testable.
