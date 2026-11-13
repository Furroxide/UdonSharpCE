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

        world.RegisterEntity(vehicle); // Safe even when the vehicle or world Start ran first.
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
| `FrameHistory` | Stores predicted snapshots for comparison with received state. |
| `RollbackManager` | Measures historical prediction error and applies a correction at the present. |
| `StateCompressor` | Quantizes and packs physics snapshots. |
| `SyncPrioritizer` | Prioritizes which entities to send first. |

## NetPhysicsWorld Highlights

| Member | Description |
| --- | --- |
| `TickRate` / `MaxTicksPerFrame` | Simulation pacing. |
| `AutoSimulate` | Run simulation in `FixedUpdate`. |
| `RegisterEntity()` / `UnregisterEntity()` | Manage entities manually if needed. |
| `Simulate()` / `SimulateSingleTick()` | Advance logical ticks. Manual callers must also call `ApplyVehicleForces()` once per FixedUpdate. |
| `BroadcastState()` / `ReceiveState()` | Send and receive snapshot data. |
| `MaxEntitiesPerStatePacket` | Default cap 32; actual cap also respects the packet and state bandwidth budgets. |
| `StateBoundsMin` / `StateBoundsMax` | Position encoding box relative to the world transform, shared by all clients. |

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
| `InputEnabled` | `true`; false publishes neutral input while recording continues. |
| `RedundantFrames` | `10`; increases with send interval to overlap the preceding packet, within history capacity. |
| `SendRate` / `MinSendRate` | `15` / `5` Hz. |
| `InputBytesPerSecondBudget` | `4096`, shared across driven vehicles; the minimum send rate and redundancy can exceed this soft budget. |
| `UseStickForDodge` | `true` |

Input comes from VRChat input callbacks rather than configurable Unity input axes.

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
- Set `NetPhysicsWorld.StateBoundsMin/Max` to cover all motion relative to the world transform. Positions outside the box clamp to its surface. The defaults reach 100 m to either side of the world object horizontally, and from 10 m below it to 150 m above, which is higher than a default `NetVehicle` can climb even with no drag. Each axis is quantized to 65,536 steps across the box, so with the defaults a position arrives within about 1.5 mm per axis; a larger box costs precision in proportion.
- Leave `_entityId` at -1 and the world numbers entities by their place in the scene hierarchy (sibling index from the scene root down, ancestors first), stepping over any id set by hand, so every client agrees whatever order the entities started in. Set the id by hand for an entity that shares its GameObject with another or is reparented or reordered at runtime.
- `NetVehicle.MaxThrottle`, `MaxBrake` and `MaxBoost` are forces in newtons and `JumpImpulse`, `DodgeImpulse` and `DoubleJumpImpulse` are impulses in newton-seconds, so a heavier `Rigidbody` accelerates less. `VehiclePreset` derives them from mass, top speed and time to reach it.
- `NetVehicle.GroundedThreshold` is how far below the underside of the vehicle's `BoxCollider` the ground may be for it to count as grounded. Triggers never count as ground.
- Use `VehiclePreset` assets to share tuning across vehicles.

## Current Limitations

- State packets default to a 32-entity cap and are further limited by payload size and the configured bandwidth budget.
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

Assign `World` in the inspector, or set it from code and register the entity yourself: an entity registers itself only in its own `Start`, which may already have run.

```csharp
void Start()
{
    vehicle.World = world;
    world.RegisterEntity(vehicle);
}
```

## Correction Model

Udon cannot advance PhysX manually. `RollbackManager` therefore compares authority state with local history at the corresponding input frame, applies the measured position/rotation/velocity delta to the present and affected history, and blends the remaining offset. Without matching history it extrapolates the received pose by velocity. This is state correction, not a rewind and replay of collision physics.

For the vehicle you drive, the corresponding frame is the one stamped on the newest input the authority had applied, which it sends with the car's state. When the authority has no such figure yet, that car is only corrected once it is further out than `CorrectionSnapDistance`. Position error below `RollbackManager.PositionThreshold` plus about one and a half physics steps of travel is ignored, since two clients sample the same frame number up to a physics step apart.

Logical ticks handle timers, boost drain and input edges. Continuous forces run once per FixedUpdate from the newest input, including physics steps with no new tick. Becoming authority and explicit restores clear pending correction offsets. Priority debt lets lower-priority entities eventually receive a state slot.
