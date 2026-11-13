# Changelog

Notable changes to UdonSharp CE. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Changes to the underlying UdonSharp compiler inherited from
[upstream](https://github.com/MerlinVR/UdonSharp) are not repeated here.

## [Unreleased]

## [1.2.0] - 2026-11-13

The first UdonSharp CE release.

### Added
- `UdonSharp.CE.Net.Authority` — declares at the field who may write a synced value.
  `[Sync(Authority.Owner)]` keeps the strict ownership check; `[Sync(Authority.Predicted)]` states
  that every client writes it locally and the owner's value reconciles it. Authority is a required
  positional argument, because wrong interpolation merely looks janky while wrong authority is a
  silent desync.
- `CE0063` — reports an `Authority.Predicted` field on a behaviour whose declared sync mode
  replicates no variables, so the prediction can never converge.
- `[SyncOnJoin]` state that arrives before a behaviour's `Start` is now applied when `Start` runs.
  VRChat drops an `OnDeserialization` that arrives before `Start`, so a late joiner whose object was
  inactive used to keep the scene defaults until the owner's next change.
- `CE0011` warns when `[Rpc(RateLimit = n)]` sits next to a hand-written `[NetworkCallable(m)]` with a
  different rate (`m` is the one VRChat enforces).
- Async methods report syntax UdonSharp cannot compile (`goto`, `try`, `?.`, tuples, local functions
  and the like) as `CE0025` at the author's own line, instead of an error inside the generated state
  machine.
- `as` to Unity, VRChat and system types is a real runtime type test (`null` on a mismatch).
- `NetPhysicsWorld.StateBoundsMin/Max` (state-encoding box, relative to the world object) and
  `InputRecorder.InputEnabled` (send neutral input while the local driver is away).
- `CEDebugConsole.OnFilterDropdownChanged()` / `OnAutoScrollToggleChanged()` — parameterless handlers a
  uGUI control can actually reach, with optional Dropdown/TMP_Dropdown/Toggle references.
- The showcase arena has a HUD: a scoreboard over the centre spot (score, clock, match overlays and the
  local player's team) that turns to face each player, a boost gauge over the local player's own car,
  and a how-to-play board where players arrive. The pool has four cars instead of one, each painted in
  its driver's team colour.
- A regression suite for everything below: the edit-mode `CEShowcase.Tests` assembly grows from 107 to
  553 tests, including tests that compile fixtures with UdonSharp and run them in the Udon
  VM, and a guardrail that fails if any runtime-library method can re-enter itself without
  `[RecursiveMethod]` (Udon gives such a method one set of parameter slots).

### Changed
- `CE0014` is authority-aware: `Predicted` fields are exempt, `Owner` fields get the strict check,
  and bare `[UdonSynced]` keeps its existing inference path unchanged.
- **Breaking:** `[Sync]` now requires an `Authority` argument. `[Sync]` and
  `[Sync(InterpolationMode.X)]` no longer compile; interpolation moves to the second positional
  argument or the named `Interpolation` property.
- **Breaking (CE.NetPhysics, beta):** rollback no longer rewinds and replays. Udon cannot step PhysX,
  so the "replay" only re-applied forces into one physics step and snapped bodies to an old pose.
  `RollbackManager` now measures the error at the server's frame and corrects it at the present.
  Vehicle forces are in newtons (`ForceMode.Force`), the state packet carries each vehicle's input lag
  (25-byte entity records), entity ids follow the scene hierarchy rather than `Start` order, and
  `RollbackManager.Reconcile` takes the local player id.
- A synced `string` no longer forces Manual sync: `CE0034` rejects a declared Continuous mode only for
  synced arrays and `[SyncOnJoin]`, and a string infers Manual only when no field is interpolated.
- A parameterless `[Rpc]` without `RateLimit` is compiled as `[NetworkCallable(5)]`, the SDK default.
  It used to emit a rate of 0, which the SDK rejects, so the behaviour did not compile.
- The package depends on `com.vrchat.worlds` `^3.8.1` (network-callable events).
- `UdonSharpCompileOptions.EnableAssemblyOptimization` defaults to `false` and is documented as
  unused. It was documented as "enabled by default" for an optimizer that has no call site; its passes
  are not sound and stay unwired.
- GraphBridge wrapper classes are named `<Category>GraphWrappers` and calls are `global::`-qualified;
  generated files are written only when they change, refused if they do not parse, and stale ones are
  removed.
- The CE Inspector's "Use CE Inspector" preference reflects UdonSharp's Default Behaviour Editor
  setting, which is the switch that actually decides.
- Showcase arena cars are handed out when a player arrives in the arena and handed back when they return
  to the hub, respawn or leave the instance, instead of going to the first players to join the instance
  wherever they were. Teams are balanced over the players who drive, and a car that joins a match in
  progress is put down clear of the others.
- Releases cut by hand (Build Release, run manually) are drafts by default, and every release takes its
  notes from its version's section of this file.

### Fixed

**Compiler**
- Reading two static fields of a user class in one expression (`Cfg.A + Cfg.B`) read a stale copy for
  the first once the type was initialized.
- `foreach` kept its enumerator in a slot shared by every `GetEnumerator` call of that collection type,
  so nested loops over two lists (or a `HashSet.UnionWith` inside a loop) ended early. Collections now
  hand out one enumerator per loop.
- A `[RecursiveMethod]` call on one of its own parameters (`n.Count(n.next)`) used the overwritten
  parameter as its receiver.
- Object creation was written into the assignment target before its arguments and initializer values
  were read (`c = new Color(1, 1, 1) { a = c.a }`).
- A method called only from a static field initializer was never emitted.
- Constructor chaining, explicit static constructors, `as` to user and UdonSharp types, `foreach` over
  `CEList`/`CEDictionary` and user-defined conversion operators used to compile into wrong programs or
  internal errors; each is now a compile error at the line that uses it.
- `sizeof(char)` folded to 1.
- Program-asset auto-creation could overwrite an unrelated asset at `<Script>.asset`, or adopt another
  script's program asset.
- An asset-import error anywhere in the console blocked every UdonSharp compile; a world build no
  longer proceeds past real C# compile errors.
- `List`/`Queue`/`Stack` with zero capacity never grew, `Stack.TryPop` read past the top,
  `Stack.ToArray` returned bottom-first, `List.Insert` at the end was rejected, `Dictionary`/`HashSet`
  `Count` and `Remove(key, out value)` ignored serialized contents until first touched, `Add` re-entered
  itself while loading serialized contents, and the Queue serializer called the wrong `Enqueue`.

**Code generators**
- Async: `break`/`continue` in an awaiting loop hung or broke the build; named arguments to an awaited
  child bound to the wrong parameters; `#if` inside a lowered statement was re-evaluated without the
  project's symbols; any `await` in non-Udon code failed the whole compile; a child that finished
  without waiting nested one event per iteration until VRChat halted the behaviour, and a refused
  re-entrant call could leave its caller never resumed; attributes, `ref`/`out` semantics, array
  initializers, `nameof`, `const` locals and statements after a `return` were mishandled.
- `[SyncOnJoin]`: an early `return` in `OnPlayerJoined`/`OnPreSerialization` skipped the capture; an
  owner calling its own `OnDeserialization()` reverted its change; a subclass with no fields of its own
  never captured or restored; an out-of-range number in a payload halted the behaviour; sealed, inherited
  and `DeserializationResult`-overload behaviours produced code UdonSharp rejects; `long` text depended
  on the client's culture.
- `[Sync(Quantize)]` helpers on a base and a subclass collided, and a subclass handler could skip the
  base's rounding.
- `[Rpc]`: implicit argument conversions were dropped at the rewritten call site (the event was sent
  with the wrong type and refused); aliased or derived attribute types were not recognised at the
  declaration; a `static` `[Rpc]` compiled and never ran.
- Sync-mode inference looked at one declaration only: a partial behaviour got two
  `[UdonBehaviourSyncMode]` attributes (CS0579) and a subclass could run Continuous under its base's
  Manual-only payload.
- `ForEach` lambda inlining never produced code that compiled, rewrote `List<T>.ForEach` in any class
  (breaking plain MonoBehaviours), and turned a lambda's `return` into a return from the method.
- `Action` → `CECallback` used names Udon does not export (`Interact` instead of `_interact`), missed
  callers in other files, and failed the compile over plain MonoBehaviours.
- `CELogger.Log(..., LogLevel.Error)` was logged as plain `Debug.Log`.
- Persistence converters: a `null` string saved once halted every later `Restore`, array fields did not
  compile, `[Range(-50, 50)]`-style arguments were dropped and doubles were parsed with the machine's
  culture, duplicate keys were accepted, and `long` values did not survive the round trip.
- The USCE0001 tripwire failed correct projects (`[RpcLogger]`, `counts[RpcKind.X]`, an `[Rpc]` that
  already carried everything the emitter would add, `[SyncOnJoin]` fields that were all unsupported).

**Analyzers and validators**
- `CE0010` warned about `Quantize` on `float`/`double`; `CE0003`/`CE0030` sized every primitive as 4
  or 10 bytes.
- `CE0014` reported handlers reached only through owner-targeted events or owner-armed timers, missed
  writes through struct members, and let one exempt caller exempt every path.
- `CE0016` reported subclasses whose base declares the synced fields; `CE0017` ignored change gates in
  the caller and treated null checks as gates; `CE0003` and `CE0010` ignored a sync mode declared on a
  base class.
- `CE0030` and the Persistence Size validator never saw a `[PlayerData]` model.
- `CE0011` warned on the valid `OwnerOnly` + `Target = Others`; `CE0012` ignored the event's receiver.
- The Bandwidth Analyzer subtracted savings Quantize and DeltaEncode never make, ignored array lengths
  and base-class fields, and double-reported errors; several validators crashed, blamed the wrong
  method, or reported initialized arrays.

**Runtime libraries**
- `CEDictionary` lost the entry that triggered a resize and duplicated another under Udon, and grew
  without bound under add/remove churn.
- `CEWorld` system indices pointed at the wrong system after the first `Tick`; the pending-destroy queue
  drifted and could drop a destroy; `ClearEntities` kept entity versions. `CEGrid` rejected positions on
  the max bound and returned nothing for an unbounded query. `DataTokenConverter` turned every JSON
  number into 0 for non-`double` types. `BatchProcessor` starved tail items, `CEPool` let stale handles
  release a new owner's slot, and nested `CEList` enumeration stopped early.
- `CERandom` halted Udon for negative seeds and the parameterless constructor, and `Range` broke for
  wide spans; `CENoise.Simplex3D` indexed out of range; `CEDungeon` and `WFCSolver` used
  multidimensional arrays UdonSharp cannot compile, plus several logic defects.
- `CEDebugConsole`/`CEProfiler` halted when used before their `Start`; nested profiler sections lost the
  outer one; `PerformanceMonitor`'s first reading was inflated.
- `ConflictResolver` padded the shorter array with zeros.
- NetPhysics: positions were clamped to a ±100 m box most of the shipped arena lies outside; the ground
  probe never left the car's own collider; forces were in the wrong units; frame history slid forward
  on every correction; blends never finished on a moving body; input tables stopped at player id 80;
  entities registered before the world were discarded; a NaN position could halt the authority.

**Showcase world**
- Only the instance master's leaderboard score synced; the stress test overwrote the shared roster;
  bullets spawned on Station 6; the flock ran in the hub and stuck to its walls; the Rocket League car
  drove from anywhere in the world and a freed car never reached a waiting player; showing the theater
  turned the stage lights on; the arena HUD allocated an unused player array every frame; plus fixes to
  the HUD state, cutscene buttons, achievements, chat events and late-join state.

**Editor tools**
- "Simulate Late-Join" zeroed every synced field in an edit-mode scene and could not restore them.
- Generated GraphBridge wrappers could call themselves forever or fail to compile.
- The CE Inspector dropped multi-object editing; the network simulator's statistics and ordering were
  wrong.

**CI and packaging**
- Releases were created under a bare version tag beside the pushed `v` tag; a release cut by CI never
  rebuilt the VPM listing; an unreachable GitHub API published an empty listing; only the first 100
  releases were read.
- The "Community Edition" package sample imported placeholder files (including corrupt `.asset` and
  `.meta` files).

### Removed
- `AsyncMethodAnalyzer` and `CE0020`, which could never report: analyzers run after the async
  transformer has already lowered or rejected every async method.
- `CE0061`. A non-behaviour that uses `Action` is now left as written instead of failing the compile.
- The "Community Edition" package sample and the orphaned `Samples~/CEOptimizationExamples.cs`.
- Upstream CI that cannot run on this fork (VRChat CLA enforcement, Netlify previews, the private
  VRChat docs builder), and the DocFx/Nuke doc pipeline, which regenerated `Tools/Docusaurus/docs`
  from a source directory that does not exist here and would have deleted the hand-written docs.
