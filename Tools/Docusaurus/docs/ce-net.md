# CE Net

CE.Net provides compile-time annotations and helper utilities for networking analysis and safer RPC usage.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ScoreBoard : UdonSharpBehaviour
{
    // Authority is required and positional: it says who is allowed to write the field.
    // Owner means one client decides the score and the rest are told, so CE0014 checks that
    // every write to it is ownership-guarded.
    [Sync(Authority.Owner)]
    public int score;

    // Any player can ask for points. RateLimit is compiled into [NetworkCallable], so VRChat
    // enforces it — do not also wrap the body in a RateLimiter, that throttles twice.
    [Rpc(Target = RpcTarget.Owner, RateLimit = 2f)]
    public void AddScore(int delta)
    {
        // Target = Owner means this body only ever runs on the owner, which is the only client
        // whose write to a synced field is kept. Without that, every non-owner's write is
        // silently discarded and the score simply never changes for anyone.
        score += delta;
        RequestSerialization();
    }

    public override void Interact()
    {
        // Written as an ordinary call; CE rewrites it into SendCustomNetworkEvent(Owner, ...).
        AddScore(10);
    }
}
```

## Key Concepts

- `[Sync]` inherits `UdonSyncedAttribute`, so it uses UdonSharp's upstream synced-variable pipeline. Do not put both `[Sync]` and `[UdonSynced]` on one field.
- `[Sync]` takes an `Authority` as its first, required argument. See [Sync Authority](#sync-authority) — it is what tells the analyzers whether an unguarded write is a desync or the point.
- `[UdonSynced]` is unchanged and fully supported. Nothing about the upstream attribute moved, and it has no authority parameter. The difference is precision: a declared authority replaces the inference CE0014 otherwise has to fall back on.
- `[Rpc]` is real. The receiver is marked `[NetworkCallable]` so VRChat enforces the declared `RateLimit` itself, `OwnerOnly` becomes an actual receiver-side ownership check, and calls to the method are rewritten into `SendCustomNetworkEvent` with the declared target. Write `recv.Foo(a, b)` and it goes over the network; `RpcTarget.Self` stays a direct call. Use `CENet.Rpc(target, RpcTarget.X, nameof(Foo), …)` when you would rather the send be explicit at the call site.
- A rewritten call sends each argument as the declared parameter type. `SetVolume(1)` against `void SetVolume(float v)` sends `1f`, as the direct call would have: VRChat refuses an event whose argument types do not match the receiver's parameters, so the conversion is spelled out for you rather than lost.
- Every `[Rpc]` has a rate. `RateLimit` left unset is not "unlimited" — the method gets `[NetworkCallable]`'s default of 5 events per second, parameterless or not, because the SDK rejects any network-callable method with a rate below 1.
- Behaviour sync mode is inferred when you declare none: Manual if the behaviour's synced state requires it (a synced array, or `[SyncOnJoin]` state), Manual as well for a synced `string` unless some field is interpolated, and Continuous otherwise. "The behaviour's synced state" means all of it — every partial part of the class and every base class — so a derived behaviour that inherits an array or `[SyncOnJoin]` state is Manual even if its own fields would not ask for it. A mode you declare is never overridden; it is only rejected (CE0033, CE0034) when it cannot carry the state at all.
- `[SyncOnJoin]` emits JSON glue for supported pure data graphs and transports it as an `[UdonSynced]` string on the behaviour itself, written in `OnPreSerialization` and applied in `OnDeserialization`. Authority is normal object ownership, and VRChat delivers the value to late joiners automatically — there is no coordinator object.
- The payload is also applied in `Start`, before your own `Start` body runs. VRChat drops an `OnDeserialization` that arrives before a behaviour has started — the usual case for an object that is inactive when a player joins — so without this such a behaviour kept its scene defaults until the owner next changed something. Applying an empty or already-applied payload does nothing and logs nothing. A base behaviour gets a generated `Start` only while no behaviour derived from it declares one (UdonSharp would reject the pair); the derived `Start` then carries the restore instead.
- A serialization VRChat does not deliver is not retried. The glue logs an error when one group's payload alone is over the Manual sync limit (`NetworkLimits.ManualSyncMaxBytes`, estimated at 2 bytes per character), and capture only asks to serialize again once some payload changes. A `Group` does not help with size: every group of a behaviour is sent in each of its serializations, so split oversized state across behaviours instead.
- `SyncOnJoin(Group = "name")` gives a behaviour one payload per group instead of one in total, each with its own capture, restore and applied-guard. Capture requests serialization only when a group's JSON changes. Decode guards are independent per group. Serialization still applies to the behaviour; groups do not guarantee that unchanged strings are omitted from a packet. Fields naming no group share the default payload.
- `RateLimiter` is a runtime helper; call `TryCall()` to enforce limits. The limit is a minimum interval, not a count: a rate of 5 lets one call through and then counts every call in the next 0.2 s as over the limit, so of two calls 50 ms apart the second is refused (or, with `DropOnLimit` off, let through with a warning). It reads `Time.time`, which only advances once per frame, so with a rate set no more than one call per frame is ever within the limit.
- `[LocalOnly]` is a compile-time check on your own sends, not a runtime barrier. CE0012 reports a behaviour sending a network event to one of its own `[LocalOnly]` methods — through `SendCustomNetworkEvent` or `CENet.Rpc`, with the event name written as a string literal or `nameof`, and sent to the behaviour itself or to a reference of its own (or a derived) type. A send to some other behaviour type is not reported, whatever the event is called.
- `MergeStrategy` and `ConflictResolver` are manual utilities for resolving conflicting sync state.

## Sync Authority

`[Sync]` requires an `Authority` as its first argument. It declares who is allowed to write the
field, which is the one thing about a synced variable that cannot be recovered by reading the code
around it.

VRChat treats every synced variable identically on the wire: only the owner's copy is ever
serialized, and every other client's copy is replaced by whatever the owner last sent. A write on a
non-owner is not rejected and does not error — the local copy really does change, it is simply
never transmitted, and the owner's next deserialization overwrites it. Whether that is a bug or the
entire point depends on what the field is for, and only the author knows.

| Value | Meaning | What checks it |
| --- | --- | --- |
| `Authority.Owner` | Only the owner may write. A write anywhere else is a silent desync: correct on exactly one client, and briefly. | CE0014 requires every write to sit behind an ownership check, or on a path that takes ownership first. |
| `Authority.Predicted` | Every client may write this locally — usually as simulation, but a value each client latches from a broadcast counts too. The owner's value is authoritative and replaces the local copy on the next deserialization. | CE0014 does not apply — the unguarded write is the prediction — and nothing replaces it. CE0063 covers only the provable corner: a behaviour whose sync mode replicates no variables at all, which CE0033 normally rejects during codegen first. Whether the owner then publishes on a cadence is review, not analysis; see [Current Limitations](#current-limitations). |

`Owner` is `0`, so a defaulted or zero-initialized value is the strict reading.

### Choosing

Pick `Owner` for state one client decides and the rest are told about: scores, game phase, who is
holding what, whether a door is unlocked. When in doubt pick `Owner` — it is the reading that
reports a desync rather than permitting one.

Pick `Predicted` for state every client writes for itself and that stalls visibly if it waits for a
packet: a boost meter draining, a jump or dodge cooldown counting down, a projectile advancing
between snapshots, or a broadcast value each client applies the moment it hears it. Authority is
about **who may write the field**, not about where the value came from — deriving it locally is the
common reason a client has one to write, not the definition.

Do not pick `Predicted` to quiet CE0014. Authority is declared per field, not per write, so marking
a field `Predicted` also switches the check off for the genuinely owner-only writes to that same
field.

### Predict-and-reconcile, worked

`NetVehicle` is the shipped example, and the reason this attribute exists. Its four synced fields
split cleanly:

```csharp
[Sync(Authority.Predicted)] private int   _ownerPlayerId = -1;
[Sync(Authority.Predicted)] private float _boostRemaining;
[Sync(Authority.Predicted)] private float _jumpTimer;
[Sync(Authority.Owner)]     private byte  _airStateFlags;
```

`NetPhysicsWorld` steps every registered vehicle on every client, so boost drain and the jump
cooldown advance locally everywhere. That is what keeps a remote car's boost meter and dodge timing
moving between packets instead of stepping only when one lands. Those writes are unguarded on
purpose — before authority was declarable they were five CE0014 warnings on correct code, and the
only way to clear them was to stop predicting. The owner's `RequestSerialization` publishes the real
values and `OnDeserialization` replaces the local guesses, which bounds how far they can drift.

`_ownerPlayerId` is the interesting one, because it looks like owner state and is not. Every client
that hears the `AssignDriver` broadcast latches the id locally, so input routing starts on that frame
instead of stalling until the owner's next serialization, and the owner re-publishes it afterwards.
Authority records **who may write the field**, not where the value came from: the write happens on
every client, so the field is `Predicted` even though the value originated in a broadcast rather than
in local integration.

`_airStateFlags` is the only genuinely owner-written field here. Its single write is in
`OnPreSerialization`, which runs on the serializing client and nowhere else, so it stays `Owner` and
CE0014 keeps checking that no other path starts writing it.

A grant is not a prediction. When a boost pad tops the meter up, that is new information no client
can derive on its own, so it runs on the owner and travels the normal way — even though
`_boostRemaining` is otherwise a predicted field.

### Relationship to `[UdonSynced]`

`[UdonSynced]` is unchanged, still fully supported, and still the right thing to write when you do
not want CE's attribute. A bare `[UdonSynced]` field behaves exactly as it always has: CE0014 infers
authority from the code around the write, scanning the path for `Networking.IsOwner` and following
one level of indirection through a cached flag or property. That inference is a guess about intent.
It is right most of the time, and when it is wrong it is wrong in the direction of warning about
correct code, because it has no way to represent "every client writes this on purpose".

`[Sync(Authority...)]` replaces the guess with a statement, which is what lets the analyzers stop
reporting correct code: `Owner` keeps the strict check, and `Predicted` switches it off. `Predicted`
is not a swap for an equivalent check on the other side — CE0063 covers only the behaviour that
replicates nothing at all, and CE0033 normally rejects that during codegen first. See
[Current Limitations](#current-limitations).

## API Reference

### Sync Attributes

| Attribute | Description |
| --- | --- |
| `Sync` | Marks a Udon synced field and declares its `Authority` (required, positional). Adds optional interpolation, quantization, and advisory delta metadata. |
| `SyncOnJoin` | Marks fields for generated late-join state sync. `Group` splits them across independent payloads; `Priority` orders them within one. |
| `SyncSerializer` | Specifies custom serialization for `SyncOnJoin` fields that cannot use generated glue. |

### RPC Attributes

| Attribute | Description |
| --- | --- |
| `Rpc` | Marks a method network-callable and rewrites its call sites into `SendCustomNetworkEvent`. |
| `RpcOwnerOnly` | Shorthand for `Rpc(OwnerOnly = true)`. |
| `LocalOnly` | Marks methods the declaring behaviour must not send a network event to (CE0012). A compile-time check, not a runtime barrier. |

### Enums

| Enum | Values |
| --- | --- |
| `Authority` | `Owner`, `Predicted` |
| `InterpolationMode` | `None`, `Linear`, `Smooth` |
| `RpcTarget` | `All`, `Owner`, `Others`, `Self` |
| `MergeStrategy` | `LastWriteWins`, `OwnerWins`, `MasterWins`, `HigherWins`, `LowerWins`, `Additive`, `Custom` |

### Utilities

| Type | Purpose |
| --- | --- |
| `RateLimiter` | Spaces calls at least 1 / rate seconds apart. |
| `NetworkLimits` | Constants for sync and RPC limits. |
| `ConflictResolver` | Manual helpers for ownership and merge strategies. |
| `MergeStrategyAttribute` | Annotates `[SyncOnJoin]` fields for conflict handling (CE0046 elsewhere). The glue does not yet apply the strategy, so call `ConflictResolver` yourself. |

## Current Limitations

- Applying both `[Sync]` and `[UdonSynced]` to the same field is invalid; use one or the other.
- **Breaking change.** `Authority` is a required positional argument, so `[Sync]`,
  `[Sync(InterpolationMode.Linear)]` and `[Sync(Quantize = 0.1f)]` no longer compile. Add the
  authority: `[Sync(Authority.Owner)]`, `[Sync(Authority.Owner, InterpolationMode.Linear)]`,
  `[Sync(Authority.Owner, Quantize = 0.1f)]`. `Authority.Owner` is the mechanical migration and
  changes no behaviour — CE0014 already assumed every synced field was owner-authoritative, so
  `Owner` is what your fields were already being checked as. Only promote a field to
  `Authority.Predicted` when you know every client is meant to write it locally *and* that the owner
  publishes a value to reconcile against — the second half is not checked for you. `[UdonSynced]`
  call sites are untouched.
- `Authority.Predicted` takes CE0014 off the field and puts nothing equivalent in its place. Know how
  little CE0063 covers before you rely on it: it warns only when the declaring behaviour's sync mode
  replicates no variable at all (`None` or `NoVariableSync`). It does **not** verify that a Manual
  behaviour publishes from the owner, because failing to find an owner-reachable
  `RequestSerialization()` is not evidence that there is none — a manager behaviour can call
  `other.RequestSerialization()`, and an inspector-wired `SendCustomEvent` is invisible to source
  analysis. CE0015 reports the common form (Manual, writes synced fields, no `RequestSerialization()`
  in its own source); anything past that is review. CE0063 is also a backstop rather than a routine
  check: CE0033 rejects a synced field on a `None`/`NoVariableSync` behaviour during codegen, before
  analysis runs, so in a normal compile CE0063 reports nothing. Authority is also per field, not per
  write, so a predicted field's owner-only writes lose CE0014 coverage too.
- `Quantize` rounds the field itself, on the owner, each time the behaviour serializes — not a copy made for the wire. The owner's value snaps to the grid as well, and a change smaller than half a step between two serializations never accumulates, because the next serialization rounds it straight back: a meter drained by `0.02f * Time.deltaTime` with `Quantize = 0.1f` never moves. To send a quantized value while keeping a precise one, keep the precise value in an unsynced field and assign the synced field from it (`charge = _charge;`). Ties round to even. `DeltaEncode` / `DeltaEncodeHint` is advisory only; VRChat does not expose true delta sync for Udon fields.
- `[SyncOnJoin]` generated glue supports `bool`, the integer types, `float` and `double` (not `char` or `decimal`), enums, strings, supported Unity value structs, `DataDictionary`/`DataList`, one-dimensional arrays, `List<T>`, and Unity-serializable pure data models. An enum from UnityEngine, VRChat or System has to be backed by `int` with no negative value and none of 2048 or more, because UdonSharp turns a number back into one through a lookup table. A decoded number that does not fit its field — 300 for a `byte`, a value outside an enum's table — rejects that group's payload like any other malformed entry rather than halting the behaviour.
- Scene/object references, delegates, polymorphic fields, dictionaries other than `DataDictionary`, jagged/multidimensional arrays, and recursive graphs need `[SyncSerializer]`. CE0032 warns when a root field is omitted from generated glue, including a `static` or `const` field, which has no per-instance state to send.
- `Group` names become part of generated member names, so characters outside `a-z A-Z 0-9 _` become underscores and two group names that differ only in punctuation are a compile error rather than a silent merge into one payload. `Priority` orders fields inside a single group only — two groups are two synced variables and VRChat may deliver them in either order, so a dependency that has to hold belongs in one group.
- `[Rpc]` sends for you: the receiver is marked `[NetworkCallable]` and call sites become `SendCustomNetworkEvent`.
  Limits worth knowing: `RateLimit` is clamped to whole events/second in 1..100 and a clamp is reported as a
  console warning during the compile; a `RateLimit` you do not set is 5 events/second, not unlimited; a
  `[NetworkCallable(n)]` you write yourself is left alone and its `n` is the rate in effect, so a `RateLimit` beside
  it that says otherwise is ignored (CE0011 warns when the two differ); an `[Rpc]`
  calling another `[Rpc]` is CE0045 (direct self-recursion is allowed) — route deliberate hops through `CENet.Rpc`;
  parameters must be Udon-serializable and at most 8; the method must be an instance method (a `static` one is
  CE0011, since UdonSharp exports no static method as an event) and must return `void`.
- `OwnerOnly` checks who *sent* the event; `Target` chooses who *receives* it. They are independent, so
  `Target = Others` with `OwnerOnly` is the ordinary "owner announces to everyone else". The pairing that does
  nothing useful is `Target = Owner` with `OwnerOnly`: only the owner calling its own copy ever passes, and
  CE0011 warns about it. A request other players send to the owner wants `Target = Owner` *without* `OwnerOnly`.
- `RpcTarget.Self` describes the call sites — they stay direct calls — not the method. The method is still
  marked `[NetworkCallable]`, so an explicit `SendCustomNetworkEvent` or `CENet.Rpc` can still reach it.
- What happens to an event past its rate limit is VRChat's decision and is not configurable from `[Rpc]`. The SDK
  exposes `NetworkCalling.GetQueuedEvents` / `GetAllQueuedEvents` for reading how many events are still waiting.
- `MergeStrategyAttribute` does not auto-resolve conflicts; call `ConflictResolver` yourself.

## Common Pitfalls

### Bad

```csharp
using UdonSharp.CE.Net;

public class BadSync : UdonSharpBehaviour
{
    [UdonSynced]
    [Sync(Authority.Owner)] public int value; // Invalid: duplicate synced attributes

    [Sync] public int other;                  // Invalid: Authority is required
}
```

### Good

```csharp
using UdonSharp;
using UdonSharp.CE.Net;

public class GoodSync : UdonSharpBehaviour
{
    // Owner-authoritative, no interpolation. Interpolation is second-positional or named now,
    // because a wrong interpolation only looks janky while a wrong authority silently desyncs.
    [Sync(Authority.Owner)]
    public int value;

    // Predicted on every client, corrected by the owner's next serialization.
    [Sync(Authority.Predicted)]
    public float chargeRemaining;
}
```
