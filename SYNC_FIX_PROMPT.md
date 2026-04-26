# Sync Pipeline Fix — Continuation Prompt

Hand this prompt to a fresh Codex/Claude Code session at the project root:
`C:\Users\vanst\Code\UdonSharpCE`.

The previous implementation pass completed most of [SYNC_FIX_PLAN.md](./SYNC_FIX_PLAN.md).
Do not restart the original stage plan. Continue from the progress table at the
top of `SYNC_FIX_PLAN.md`.

---

## Prompt

```text
You are continuing the approved sync/persistence/cutscene fix work in
C:\Users\vanst\Code\UdonSharpCE.

Read ./SYNC_FIX_PLAN.md first, especially "Progress Status (2026-04-26)".
Treat completed items as done unless tests prove otherwise. Do not redo broad
implemented work or refactor unrelated code.

Current verified state:
- A1 [Sync] upstream integration is done.
- A2/A3 SyncOnJoin glue and LateJoinerSync transport are mostly done.
- A4 Quantize and DeltaEncode honesty are done.
- A5 UdonVariableStorageInterface cache locking is done.
- CEPersistence now uses VRChat PlayerData JSON SetString/TryGetString.
- InventorySystem waits for OnPlayerRestored before restore.
- CollectibleItem now uses owner-authoritative collection/respawn sync.
- AsyncMethodTransformOptimizer preserves the showcase cutscene await patterns.
- CutsceneController auto-wires missing safe references.
- Verification from the previous pass:
  - exact prior failures: 3 passed, 0 failed
  - affected EditMode fixtures: 19 passed, 0 failed
  - full UdonSharp compile: Errors=0, Warnings=0

Your job is to finish the remaining follow-up items from the progress table:

1. Add CE0032 for unsupported [SyncOnJoin] field types
   - Current gap: SyncOnJoinGlueEmitter silently skips unsupported fields.
   - Fix: unsupported [SyncOnJoin] field types must produce a clear compile
     diagnostic/error, not disappear.
   - Preserve supported matrix already implemented:
     int, long, float, double, bool, string, Vector2/3/4, Quaternion, Color,
     and one-dimensional arrays of those, plus [SyncSerializer(...)] custom
     serializers.
   - Add focused tests proving unsupported fields fail loudly and supported
     fields still emit working glue.

2. Update stale documentation
   - Search for placeholder language claiming CEPersistence.Save/Restore are
     future placeholders or that CE.Net sync is only stubbed.
   - At minimum update:
     - UDONSHARP_CE_FEATURES.md
     - Tools/Docusaurus/docs/ce-persistence.md
     - Tools/Docusaurus/docs/ce-overview.md, if still stale
   - Document the [Sync] behavior change: [Sync] now participates in the
     upstream Udon synced variable pipeline by inheriting UdonSyncedAttribute.
     Warn that applying both [Sync] and [UdonSynced] to one field is invalid.
   - Document that DeltaEncode is advisory only.

3. Re-run verification
   - Run focused EditMode tests covering:
     - CEShowcase.Tests.Editor.UdonSharpCESyncPipelineTests
     - CEShowcase.Tests.Editor.Station5PersistenceRegressionTests
   - Run a full UdonSharp compile and report the error/warning counts.
   - If feasible in the current environment, run broader package tests. If not,
     state exactly what was not run.

4. Optional only if low-risk
   - If package-level Tests~/CE organization is straightforward, add or mirror
     the new regression coverage there. Do not churn test layout if it risks
     destabilizing the verified showcase tests.
   - Live multi-client VRChat/ClientSim smoke testing is desirable, but do not
     block code completion on it if the environment cannot run it. Clearly note
     whether it was run.

Engineering constraints:
- Keep changes minimal and localized.
- Do not revert existing user or previous-session changes.
- Use existing optimizer/analyzer patterns.
- Fail loudly for unsupported sync/persistence/async patterns; do not introduce
  new silent skips.
- Prefer rg for search.
- Use apply_patch or normal editor tooling for edits; avoid unrelated formatting.

When finished, update ./SYNC_FIX_PLAN.md's progress section with what changed,
what tests passed, and any remaining gaps. Then provide a concise summary.
```

---

## Operator Notes

- This is a continuation prompt, not the original implementation prompt.
- The main correctness gap is CE0032 for unsupported `[SyncOnJoin]` fields.
- Docs are known stale because the implementation changed persistence from
  placeholder logging to real VRChat PlayerData storage.
