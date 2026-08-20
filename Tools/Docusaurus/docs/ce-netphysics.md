# CE NetPhysics

CE.NetPhysics is a networked physics framework designed for predictive, tick-based simulation with client input streaming and server-authoritative state updates.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;

public class NetPhysicsBootstrap : UdonSharpBehaviour
{
    public NetPhysicsWorld world;
    public InputRecorder inputRecorder;
    public NetVehicle vehicle;

    void Start()
    {
        // Wire references
        inputRecorder.World = world;
        vehicle.World = world;

        // Entities register themselves in Start() when World is assigned
    }
}
```

## Key Components

| Type | Purpose |
| --- | --- |
| `NetPhysicsWorld` | Tick clock, entity registry, state history, and sync coordinator. |
| `NetPhysicsEntity` | Base class for networked physics objects. |
| `NetVehicle` | Vehicle controller with jump, dodge, air control, and boost. |
| `NetBall` | Shared physics object with high-priority sync. |
| `InputRecorder` | Samples local input and sends redundant input frames. |
| `InputBuffer` | Buffers server-side inputs and applies throttling. |
| `InputPredictor` | Predicts inputs when packets are missing. |
| `FrameHistory` | Stores snapshots for rollback. |
| `RollbackManager` | Applies rollback and correction. |
| `StateCompressor` | Quantizes and packs physics snapshots. |
| `InterestManager` | Filters what entities each client receives. |
| `SyncPrioritizer` | Prioritizes which entities to send first. |

## NetPhysicsWorld Highlights

| Member | Description |
| --- | --- |
| `TickRate` / `MaxTicksPerFrame` | Simulation pacing. |
| `AutoSimulate` | Run simulation in `FixedUpdate`. |
| `RegisterEntity()` / `UnregisterEntity()` | Manage entities manually if needed. |
| `Simulate()` / `SimulateSingleTick()` | Advance simulation. |
| `BroadcastState()` / `ReceiveState()` | Send and receive snapshot data. |
| `MaxEntitiesPerStatePacket` | Caps entities per sync packet (1 to 8). |

## InputFrame Summary

| Field | Range | Description |
| --- | --- | --- |
| `Throttle` | -128 to 127 | Forward/back input. |
| `Steering` | -128 to 127 | Left/right input. |
| `Boost` | 0 to 255 | Boost intensity. |
| `Buttons` | Bitfield | Jump, dodge, handbrake, boost, use. |
| `DodgeX` / `DodgeY` | -128 to 127 | Dodge direction. |

Button flags in `InputFrame`:

- `BUTTON_JUMP`
- `BUTTON_DODGE`
- `BUTTON_HANDBRAKE`
- `BUTTON_BOOST`
- `BUTTON_USE`

## InputRecorder Defaults

| Setting | Default |
| --- | --- |
| `ThrottleAxis` | `Vertical` |
| `SteeringAxis` | `Horizontal` |
| `JumpButton` | `Jump` |
| `BoostButton` | `Fire1` |
| `HandbrakeButton` | `Fire3` |
| `DodgeButton` | `Fire2` |
| `UseStickForDodge` | `true` |

## Input Flow

- Clients record local input each frame via `InputRecorder`.
- The master buffers inputs and simulates authoritative state.
- Clients predict local state and correct using snapshots.

## Predicted vs Owner State

`NetVehicle` declares the authority of each of its synced fields, which is what tells CE's analyzers
whether an unguarded write is a desync or the design:

```csharp
[Sync(Authority.Predicted)] private int   _ownerPlayerId = -1;
[Sync(Authority.Predicted)] private float _boostRemaining;
[Sync(Authority.Predicted)] private float _jumpTimer;
[Sync(Authority.Owner)]     private byte  _airStateFlags;
```

`NetPhysicsWorld` steps every registered vehicle on every client, so boost drain and the jump
cooldown advance locally everywhere. That is what keeps a remote car's boost meter and dodge timing
moving between packets instead of stepping only when one lands, and it is why those writes are
deliberately unguarded. The owner's `RequestSerialization` publishes the real values and
`OnDeserialization` replaces the local guesses, which bounds how far they can drift.

`_ownerPlayerId` is `Predicted` for a reason worth stating, because it looks like owner state at first
glance. Every client that hears the `AssignDriver` broadcast latches the id locally so input routing
starts on that frame rather than stalling for the owner's next serialization, and the owner re-publishes
it afterwards. Authority records **who may write the field**, not where the value came from — the write
happens on every client, so the field is predicted even though the value originated in a broadcast.

`_airStateFlags` is the one genuinely owner-written field: its only write is in `OnPreSerialization`,
which runs on the serializing client and nowhere else, so it stays `Owner` and CE0014 keeps checking
that no other path starts writing it.

A grant is not a prediction. Topping the boost meter up from a pad is new information no client can
compute for itself, so it runs on the owner and travels the normal way, even though
`_boostRemaining` is otherwise predicted.

CE0014 is the only diagnostic that acts on these declarations: strict on `_airStateFlags`, switched off on
the three predicted fields. `NetVehicle` declares `Manual` sync, so CE0063 never examines it at all —
CE0063 reports only a predicted field on a behaviour whose sync mode replicates no variables (`None` or
`NoVariableSync`). Nothing checks that the owner's publish cadence actually reconciles the three predicted
fields; that is left to review. See [Sync Authority](ce-net#sync-authority) for what each role means.

## Configuration Tips

- Keep `MaxEntitiesPerStatePacket` aligned with your state budget.
- Tune `StateCompressor` bounds for your world size to improve precision.
- Use `VehiclePreset` assets to share tuning across vehicles.

## Current Limitations

- State packets are capped to 8 entities per send to stay within typical sync limits.
- `NetVehicle` expects a `Rigidbody` and uses physics forces for movement.
- Authority flows from the master; clients should treat local state as predicted.

## Common Pitfalls

### Bad

```csharp
// Missing World references prevents registration and simulation.
public NetPhysicsWorld world;
public NetVehicle vehicle;
```

### Good

```csharp
void Start()
{
    vehicle.World = world;
}
```
