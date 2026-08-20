# UdonSharpCE Sync — Status and Correction

## Correction to the previous report (2026-04-28)

The earlier version of this file declared the sync rewrite "Completed". It was not, and three of the
statements it relied on were false. They are corrected here so the record does not mislead again.

| Previous claim | Reality |
| --- | --- |
| "Unity MCP focused EditMode suite: 24 passed", listing `UdonSharp.CE.Tests.Net.*` suites | Those suites live under `Packages/com.merlin.UdonSharp/Tests~/`. Unity **excludes `~`-suffixed folders from the asset pipeline**, so there is no `UdonSharp.CE.Tests` assembly and those tests have never run. The 24 were entirely `CEShowcase.Tests`, which is exactly what a run of that assembly reports today. |
| "ClientSim is not installed in this project" — used to excuse skipping runtime verification | ClientSim **is** installed. It ships inside the Worlds SDK at `Packages/com.vrchat.worlds/Integrations/ClientSim`, with `VRC.ClientSim.csproj` at the repo root and live data in `ClientSimStorage/`. |
| "`[SyncOnJoin]` … works; synced state is the late-join source of truth" | `LateJoinerSync.SerializeAllBehaviours` ran on the coordinator's owner — pinned to the **master** — and read each registered behaviour's payload from the **master's own local heap**. It never captured state authored by any other client, so late joiners received the master's view and nothing else. |

A separate problem hid all of this: `UdonSharpCompilerV1.RunCompileTimeOptimizers` caught every CE
codegen failure, logged one `Debug.LogWarning`, and continued with the untransformed trees. The
compile then *succeeded*, emitted programs with no `[SyncOnJoin]` glue, no async state machine and no
quantization, and called `RehashAllScripts()` marking everything up to date.

## Current architecture

`[SyncOnJoin]` no longer uses a coordinator. Each behaviour carries its own synced payload — one per
`SyncOnJoinAttribute.Group`, and one in total for a behaviour that names no group:

```csharp
[SyncOnJoinGenerated, HideInInspector, UdonSynced]
protected string _CE_SyncOnJoinPayload = "";          // default group: unsuffixed, unchanged

[SyncOnJoinGenerated, HideInInspector, UdonSynced]
protected string _CE_SyncOnJoinPayload_terrain = "";  // [SyncOnJoin(Group = "terrain")]
```

written in `OnPreSerialization` and applied in `OnDeserialization`, both woven in around any handler
the author already wrote. `_CE_SyncOnJoin_RequestCapture` stays the single entry point whatever the group
count — it re-serializes every group and calls `RequestSerialization()` only if one of them actually
changed, so a write to a hot group leaves a cold group's string untouched. Consequences:

- **Authority follows object ownership.** State authored by any client reaches late joiners.
- **VRChat delivers it natively.** Synced-variable state is sent to late joiners automatically; no
  coordinator, no hidden scene object, no build-time scene mutation, no hierarchy-path IDs.
- **No 2-second world-wide poll** and no O(n²) chunk reassembly.
- **Blast radius is one behaviour**, and each gets its own ~280 KB manual-sync budget rather than
  sharing one 262 KB envelope that was resent in full whenever any single field changed.

Sync mode is now **inferred**. A behaviour with synced state and no `[UdonBehaviourSyncMode]` gets one
injected — Manual when something requires it (arrays, `[SyncOnJoin]` payloads, synced strings),
Continuous otherwise. A *declared* mode is never silently overridden: `None`/`NoVariableSync` with
synced state is CE0033, and `Continuous` with Manual-only state is CE0034.

### Declared sync authority (2026-08-20)

`[Sync]` now takes an `Authority` as its first, required argument:

```csharp
[Sync(Authority.Owner)]     private byte  _airStateFlags;   // one client decides, the rest are told
[Sync(Authority.Predicted)] private float _boostRemaining;  // every client simulates, the owner corrects
```

This exists because of the five CE0014 warnings recorded under *Verification status* below. Both roles
produce the same wire behaviour — VRChat serializes only the owner's copy and overwrites everyone else's —
so a non-owner write is either a silent desync or the entire point of the field, and no amount of flow
analysis can tell which from the source alone. CE0014 had to assume desync, because that is the direction
that reports a bug rather than hiding one, and on `NetVehicle`'s prediction that assumption was wrong five
times.

Declaring the role at the field replaces a guess with a statement, so each analyzer knows which rule it is
enforcing rather than inferring one:

- `Authority.Owner` keeps the strict CE0014 check: every write must sit on a path guarded by an ownership
  check, or on a path that takes ownership first.
- `Authority.Predicted` switches CE0014 off for the field — the unguarded write *is* the prediction — and
  **nothing takes its place**. `Authority` records who may write the field, not where the local value came
  from, and the claim that the owner republishes is taken on trust. CE0063 is a narrow backstop rather than
  a replacement, and the temptation is to overstate it: it reports only when the declaring behaviour's sync
  mode replicates nothing at all (`None` or `NoVariableSync`), which is the one shape where the prediction
  is provably unreconcilable. `NetVehicle` is Manual, so CE0063 never examines the very fields that
  motivated it. It does not attempt to prove that a Manual behaviour publishes from the owner. Failing to
  *find* an owner-reachable `RequestSerialization()` is not evidence there is none —
  `other.RequestSerialization()` from a manager behaviour serializes this one, an inspector-wired
  `SendCustomEvent` is invisible to source analysis, and an ungated call in `Update` runs on the owner
  every frame. An analyzer built to remove five false positives cannot be allowed to invent new ones;
  CE0015 covers the common unpublished case from the other side, and the rest is review.
- Bare `[UdonSynced]` is untouched: it keeps the inference CE0014 has always used, and CE0063 only ever
  looks at fields that declared an authority. Upstream sync behaviour does not change.

**Breaking change.** `Authority` is positional and required, so `[Sync]`, `[Sync(InterpolationMode.Linear)]`
and `[Sync(Quantize = 0.1f)]` no longer compile. `Authority.Owner` is the behaviour-preserving migration for
all three; interpolation moved to the second positional argument, or to a named one.

### Known limits (VRChat, not CE)

- All synced UdonBehaviours on one GameObject share a single sync method. An interpolated
  `[Sync(Authority.Owner, InterpolationMode.Smooth)] Vector3` (needs Continuous) and a `[SyncOnJoin]`
  payload (needs Manual) cannot coexist on one GameObject. This is scene composition and no compile-time
  analyzer can see it.
- Cross-*behaviour* late-join ordering is not achievable — VRChat delivers each behaviour's
  deserialization independently. `[SyncOnJoin(Priority)]` therefore orders fields *within one payload*,
  which since groups landed means within one `Group`. Two groups are two synced variables and may arrive
  in either order. Express any dependency that has to hold as data, not ordering, or keep both fields in
  one group.

## Verification status

**Verified in-editor (2026-08-18)**

- **Compile gate green.** Full C# compile clean; `UdonSharpCompilerV1.CompileSync` reports **0 errors,
  5 warnings**, and **32/32** program assets emit a program. Getting here took three real fixes: a missed
  second call site of the binder change, a `DiagnosticSeverity` ambiguity in the async transformer, and
  `VRC.Udon.Common.dll` missing from the test assembly's `overrideReferences` whitelist.
- **`CEShowcase.Tests`: 54/54 passing** (was 29–30 two sessions ago, 47/47 before `Group` landed; 7 more
  tests added with it).
- **`SyncOnJoin.Group` isolates payloads, proven on two Udon heaps.** Writing a field in one group leaves
  every other group's synced string byte-identical, and a delivery carrying one group applies that group,
  sets that group's applied-guard, and leaves the rest untouched — through the author's own
  `OnDeserialization`, not the generated trigger.
- **Whole-project sync sweep**, read off the *emitted programs* rather than source:
  `manualWithZeroSynced` **2 → 0** (`NetBall` and `NetVehicle` previously declared Manual and synced
  nothing at all), `arraysUnderContinuousOrAny` **0**, and **2** live `_CE_SyncOnJoinPayload` synced strings.
- **`[SyncOnJoin]` round-trips as Udon.** Owner captures `{"_worldSeed":987654}`, the remote receives it
  through the real `OnDeserialization` event on a genuinely separate Udon heap, and the field lands.
- **Scene/prefab audit clean.** 64 scene UdonBehaviours, 0 missing `programSource`/`serializedProgramAsset`,
  0 sync-mode mismatches against their program assets, 0 disabled behaviour hosts. Prefabs are Manual.
- **Console clean of the UniTask GUID flood** (0 hits), so real diagnostics are visible again.

The 5 remaining warnings were all CE0014 on `NetVehicle`. **This entry previously called them "honest".
That was wrong, and correcting it is what *Declared sync authority* above exists for.** `ApplyInput` and its
helpers write `_boostRemaining`/`_jumpTimer` during per-client prediction; VRChat discards those writes on
non-owners and the next deserialization corrects them — which is not the bug CE0014 describes, it is
predict-and-reconcile working as designed. CE0014 flagged them because nothing in the source distinguished
a prediction from a desync, so it assumed the worse of the two. Five warnings on correct code is a false
positive, and the remedy is a declaration at the field, not a rewrite of the vehicle.

Those two fields are now `[Sync(Authority.Predicted)]` (as is `_ownerPlayerId`, for the separate
reason that every client latches it from the `AssignDriver` broadcast). That exempts them from
CE0014 and puts nothing equivalent in its place: `NetVehicle` is Manual, and CE0063 only ever
reports a behaviour whose sync mode replicates nothing at all. **The post-change warning count has
not been re-measured in the editor**; treat "0 errors, 5 warnings" above as the pre-change number
until a compile says otherwise.

**A false alarm, recorded so it is not rediscovered**

Mid-session this file nearly gained a "critical: `[SyncOnJoin]` never applies" entry. It was wrong, and the
way it was wrong is worth keeping: an editor harness that builds `UdonBehaviour`s by hand leaves
`_hasDoneStart` false, and `UdonBehaviour.RunEvent<T0>` forwards `canRunBeforeStart: false` — so
`OnDeserialization(DeserializationResult)` returns before entering the program. That is indistinguishable
from generated glue that was never wired up. `SendCustomEvent("_onDeserialization")` worked the whole time.
Any future harness must set `_hasDoneStart`; `UdonSyncHarness` now does, with a comment saying why.

**A second editor-only trap, same shape**

`_CE_SyncOnJoin_RequestCapture` opens with `Networking.IsOwner(Networking.LocalPlayer, gameObject)`, and
the Udon EXTERN behind that call cannot run in EditMode: it fails with *"DontDestroyOnLoad can only be
used in play mode"*, which halts the behaviour before any payload is written. From the outside that looks
exactly like capture not being wired up — empty payloads, no `RequestSerialization`. It is the ownership
extern, not the glue. EditMode tests therefore drive capture per group via
`SendCustomEvent("_CE_SyncOnJoin_Serialize<group>")`, which has no gate, and cover the gate's own change
detection by running the same generated method as plain .NET.

**The first build attempt failed, and it was nothing to do with sync**

`Build & Test` aborted with `BuilderException: Failed to build the World`. The cause is three lines
further up in the Editor log, not in the console: the `AssignSceneNetworkIDs` preprocess callback
returned false with *"Failed to assign network IDs, 3 errors encountered!"*, and VRChat aborts the build
whenever an `IVRCSDKPreprocessSceneCallback` reports failure.

The three were `IncompatibleTypes` on network IDs 18/19/20 — `ScoreButton_10`, `ScoreButton_100`,
`ScoreButton_50`. Each stored `NetworkIDPair` recorded **two** `VRC.Udon.UdonBehaviour` entries while the
GameObject now carries **one**, left over from when those objects had a duplicate behaviour. Repairing
those three uncovered a fourth the SDK had never got far enough to report: an `InvalidObject` entry at ID
64 whose GameObject no longer exists.

This is pre-existing and unrelated to `[SyncOnJoin]`. It blocked *any* build of this scene, and it went
unnoticed because this project had never been built — the checks that were run all predate the preprocess
stage. Fixed by rewriting the three stale type lists to match reality and letting
`NetworkIDAssignment.ConfigureNetworkIDs` drop the dangling entry, rather than by regenerating all 65 IDs,
so every existing object keeps the ID it had. `OnPreprocessScene` now returns true from a clean disk
reload and makes no further changes; the objects added this session hold IDs 66-68 and 73-74.

Worth knowing for next time: the console shows the CE0014 warnings and a `BuilderException`, but the
actual reason is only in `%LOCALAPPDATA%/Unity/Editor/Editor.log`, and the console's own USCE0001 entries
are test noise from `CESourceGeneratorContractTests.SuppressedGenerator_TripsTheTripwire`, which trips
that tripwire on purpose.

**Still not verified, and not verifiable from the editor**

A live two-client smoke test: that a late joiner receives prior `[SyncOnJoin]` state, and that state
authored by a **non-master** client reaches a late joiner — the direct test of the authority bug this
rewrite exists to fix. Also true wire byte counts, size-proportional rate limiting, `Networking.IsClogged`
behaviour, non-owner write discard, and the bandwidth `Group` is supposed to save: the editor proves one
group's payload stops being rewritten, not that VRChat then stops putting it on the wire. ClientSim substitutes for the codec and for the
player/ownership statics, not for the transport: its remote players have no second Udon heap
(`ClientSimSyncedObjectManager.cs` says so in its own TODOs).

Do not mark this work "verified in game" until that smoke test has been run.

## The two-client smoke test

The scene now carries a subject and a readout for this, so the test is runnable rather than aspirational.

**What was added** (Station 6, because it is the only station whose `[SyncOnJoin]` state already has a
working in-world readout):

- `Station6_Procgen/SyncGroupProbe` — `SyncGroupDemo`, which mixes the two shapes `Group` separates: a
  256-int `cold` snapshot, a one-int `hot` counter, and an ungrouped label. Its emitted program carries
  three synced payloads: `_CE_SyncOnJoinPayload`, `_CE_SyncOnJoinPayload_cold`,
  `_CE_SyncOnJoinPayload_hot`. It publishes with `SendCustomEvent("_CE_SyncOnJoin_RequestCapture")`, not
  `RequestSerialization()`, so the change gate is what decides whether anything is sent.
- `Btn_BumpCold` / `Btn_BumpHot` / `Btn_BumpLabel` at local x 6.5 / 8.0 / 9.5, continuing the station's
  existing button row.
- `Station6_Procgen/UI/SyncGroupPanel` + `SyncGroupReadoutHost` — `SyncGroupReadout` reads each group's
  payload and applied-guard off the probe through `UdonBehaviour.GetProgramVariable` (the only way in;
  generated members are invisible to Unity-compiled C#) and shows per-group length, prefix, applied
  state, and a change counter.

**Setup trap.** Every station is hidden at world load. Activate station 6 with
`CentralHub/StationButton_Procgen` — *not* `StationButton_6`, which is wired to the RocketLeague
teleport. Station 6 is at world x≈42.7. `_stationActive` is plain `[UdonSynced]`, so a second client
should find station 6 already visible; if it does not, ordinary manual-sync late-join is broken and no
`[SyncOnJoin]` result below means anything.

**Test A — late-join delivery.** Client 1 joins, activates station 6, presses `Btn_SetSeed` until
SeedPanel reads 1337 (use `Btn_SetSeed`, whose presets are deterministic, not `Btn_ChangeSeed`, which
uses server time). Client 2 joins. PASS: station 6 already visible, SeedPanel reads 1337, terrain matches.
FAIL: 12345, the compiled default.

**Test B — non-master authorship.** This is the direct test of the authority bug the rewrite exists to
fix. Clients 1 and 2 in world; client 2, *not* the master, drives `Btn_SetSeed` to 9001 — `OnSetSeed`
takes ownership first, so client 2 becomes the owner. Confirm client 1 updates. Client 3 joins: PASS is
9001. FAIL is 12345, or the master's earlier value, which is the old master-only capture resurfacing.
Hard variant worth running: client 2 leaves before client 3 joins, so ownership has moved on.

**Test C — per-group isolation.** Clients 1 and 2 at station 6, both reading `SyncGroupPanel`. Client 1
presses `Btn_BumpCold` once, then `Btn_BumpHot` ten times. PASS on client 2: the `hot` line's change
counter advances ten times while `cold`'s advances exactly once and its length and prefix stay put after
that first bump; the `default` line never moves. FAIL: `cold` tracks `hot`.

**What this closes, and what it does not.** Passing A, B and C closes late-join delivery, non-master
authorship, and per-group payload/guard isolation. It closes none of: wire byte counts, size-proportional
rate limiting, `Networking.IsClogged`, or non-owner write discard. Record the `Group` result as two
separate lines — *per-group payload/guard isolation: verified in game*, and *per-group wire bandwidth:
still unverified*. `RequestSerialization()` publishes the behaviour's whole synced set, and nothing in
this project can count what VRChat actually sends. To close the second line, temporarily add
`OnPostSerialization(SerializationResult)` logging `byteCount` to the probe and bump only the hot group:
if `byteCount` still carries the cold group's bytes, `Group` buys isolation and CPU locality but no wire
bytes.

**Do not use `LateJoinSimulator` for any of this.** It reflects over the Unity-compiled C# proxy, which
contains none of the `_CE_SyncOnJoin*` members, and it only looks at fields carrying `[UdonSynced]` —
`SyncOnJoinAttribute` does not derive from it. Its "100% coverage" is a statement about proxy field
assignment, not about sync.

**Failure modes worth watching**, all pre-existing to the design rather than introduced by `Group`: a
deserialization landing before the behaviour's first `ManagedUpdate` is dropped by Udon with no retry,
and the change gate means nothing re-sends it in a quiet world; a serialization VRChat drops is never
retried, because the payload was written locally before the send; and a client that never applied a
payload republishes its own defaults if ownership lands on it — with groups that reverts one group,
which reads as corruption rather than a reset.

## Remaining work

- ~~**`NetVehicle` prediction**~~ — **built differently, 2026-08-20. Do not build the mirror fields.** The
  prescription here was to predict into unsynced mirror fields and publish from the owner. That was not
  done: it doubles the vehicle's state to silence a warning that was itself wrong, and it deletes the check
  instead of teaching it. What landed is *Declared sync authority* above — `_ownerPlayerId`,
  `_boostRemaining` and `_jumpTimer` are `[Sync(Authority.Predicted)]`, `_airStateFlags` stays
  `[Sync(Authority.Owner)]` because its only write is in `OnPreSerialization`. `NetVehicle` is Manual, so
  CE0063 does not apply to it at all; the owner-side publish cadence is the part CE0063 deliberately does
  not check, and it stays a review item. Still open on this line: re-measure the warning count against a
  real compile rather than assuming it went to zero.
- **`Tests~/CE`** — superseded and documented as never-run (see its README), but not deleted: those files
  are untracked, so deleting them before a commit would be unrecoverable. Delete after committing.
- **Two-client smoke test**, as above. Everything it needs is in the scene; it needs two accounts.
- **`CESyncRuntimeTests.asmdef` is not editor-only** (`includePlatforms: []`), so its six U# fixtures are
  parsed and run through the CE source generators on every world build. They produce no shipped Udon
  program, because a behaviour only becomes one when a `UdonSharpProgramAsset` exists and none is
  committed for them — the cost is build time and diagnostic surface, not world content. Adding
  `"includePlatforms": ["Editor"]` was tried and reverted: it fails 7 tests, because
  `UdonSharpEditorUtility.GetUdonSharpProgramAsset` returns null for a behaviour in an Editor-only U#
  assembly and `CreateProgramAssetForBehaviourType` then throws `ArgumentNullException: key`. Fixing it
  properly means teaching program-asset creation about editor-only U# assemblies.
- **13 orphaned compiled programs** (~65 KB) sit in the gitignored `Assets/SerializedUdonPrograms` from
  test runs before the TearDown cleanup existed. Unreferenced, never shipped, safe to delete by hand; new
  runs no longer add to them.
