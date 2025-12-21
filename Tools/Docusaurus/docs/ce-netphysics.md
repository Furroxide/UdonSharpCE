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
| `BoostButton` | `Boost` |
| `HandbrakeButton` | `Fire3` |
| `DodgeButton` | `Fire2` |
| `UseStickForDodge` | `true` |

## Input Flow

- Clients record local input each frame via `InputRecorder`.
- The master buffers inputs and simulates authoritative state.
- Clients predict local state and correct using snapshots.

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
