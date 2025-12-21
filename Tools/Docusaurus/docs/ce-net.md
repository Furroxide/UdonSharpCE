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
    [UdonSynced]
    [Sync(InterpolationMode.None, Priority = 0.8f)]
    public int score;

    private RateLimiter _addScoreLimiter;

    void Start()
    {
        _addScoreLimiter = new RateLimiter(2f, true, nameof(AddScore));
    }

    [Rpc(Target = RpcTarget.All, RateLimit = 2f)]
    public void AddScore(int delta)
    {
        if (!_addScoreLimiter.TryCall())
            return;

        score += delta;
        RequestSerialization();
    }
}
```

## Key Concepts

- `[Sync]` is an analysis helper. Use `[UdonSynced]` for actual sync.
- `[Rpc]` documents intent and drives analyzer warnings. You still invoke network events normally.
- `RateLimiter` is a runtime helper; call `TryCall()` to enforce limits.
- `MergeStrategy` and `ConflictResolver` are manual utilities for resolving conflicting sync state.

## API Reference

### Sync Attributes

| Attribute | Description |
| --- | --- |
| `Sync` | Adds interpolation, quantization, and delta hints for analyzers. |
| `SyncOnJoin` | Marks fields for late-join sync (scaffolding only). |
| `SyncSerializer` | Specifies custom serialization for `SyncOnJoin` fields. |

### RPC Attributes

| Attribute | Description |
| --- | --- |
| `Rpc` | Declares a method as an RPC for analyzer validation. |
| `RpcOwnerOnly` | Shorthand for `Rpc(OwnerOnly = true)`. |
| `LocalOnly` | Marks methods that must never be triggered via network events. |

### Enums

| Enum | Values |
| --- | --- |
| `InterpolationMode` | `None`, `Linear`, `Smooth` |
| `RpcTarget` | `All`, `Owner`, `Others`, `Self` |
| `MergeStrategy` | `LastWriteWins`, `OwnerWins`, `MasterWins`, `HigherWins`, `LowerWins`, `Additive`, `Custom` |

### Utilities

| Type | Purpose |
| --- | --- |
| `RateLimiter` | Enforces per-second call limits. |
| `NetworkLimits` | Constants for sync and RPC limits. |
| `ConflictResolver` | Manual helpers for ownership and merge strategies. |
| `MergeStrategyAttribute` | Annotates fields for conflict handling (manual). |

## Current Limitations

- `[Sync]` does not replace `[UdonSynced]`; it only informs analyzers.
- `Quantize` and `DeltaEncode` are hints used for analysis and estimation only.
- `[Rpc]` does not send network events automatically; it only validates signatures.
- `MergeStrategyAttribute` does not auto-resolve conflicts; call `ConflictResolver` yourself.
- `SyncOnJoin` and `LateJoinerSync` are placeholders; serialization and application are not implemented.

## Common Pitfalls

### Bad

```csharp
using UdonSharp.CE.Net;

public class BadSync : UdonSharpBehaviour
{
    [Sync] public int value; // Not synced at runtime
}
```

### Good

```csharp
using UdonSharp;
using UdonSharp.CE.Net;

public class GoodSync : UdonSharpBehaviour
{
    [UdonSynced]
    [Sync(InterpolationMode.None)]
    public int value;
}
```
