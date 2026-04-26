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

- `[Sync]` inherits `UdonSyncedAttribute`, so it uses UdonSharp's upstream synced-variable pipeline. Do not put both `[Sync]` and `[UdonSynced]` on one field.
- `[Rpc]` documents intent and drives analyzer warnings. You still invoke network events normally.
- `[SyncOnJoin]` emits JSON glue for supported pure data graphs and is transported by `LateJoinerSync`.
- `RateLimiter` is a runtime helper; call `TryCall()` to enforce limits.
- `MergeStrategy` and `ConflictResolver` are manual utilities for resolving conflicting sync state.

## API Reference

### Sync Attributes

| Attribute | Description |
| --- | --- |
| `Sync` | Marks a Udon synced field and adds interpolation, quantization, priority, and advisory delta metadata. |
| `SyncOnJoin` | Marks fields for generated late-join state sync. |
| `SyncSerializer` | Specifies custom serialization for `SyncOnJoin` fields that cannot use generated glue. |

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

- Applying both `[Sync]` and `[UdonSynced]` to the same field is invalid; use one or the other.
- `Quantize` rounds float-compatible values before sync. `DeltaEncode` / `DeltaEncodeHint` is advisory only; VRChat does not expose true delta sync for Udon fields.
- `[SyncOnJoin]` generated glue supports primitives, enums, strings, supported Unity value structs, `DataDictionary`/`DataList`, one-dimensional arrays, `List<T>`, and Unity-serializable pure data models.
- Scene/object references, delegates, polymorphic fields, dictionaries other than `DataDictionary`, jagged/multidimensional arrays, and recursive graphs need `[SyncSerializer]`. CE0032 warns when a root field is omitted from generated glue.
- `[Rpc]` does not send network events automatically; it only validates signatures.
- `MergeStrategyAttribute` does not auto-resolve conflicts; call `ConflictResolver` yourself.

## Common Pitfalls

### Bad

```csharp
using UdonSharp.CE.Net;

public class BadSync : UdonSharpBehaviour
{
    [UdonSynced]
    [Sync] public int value; // Invalid: duplicate synced attributes
}
```

### Good

```csharp
using UdonSharp;
using UdonSharp.CE.Net;

public class GoodSync : UdonSharpBehaviour
{
    [Sync(InterpolationMode.None)]
    public int value;
}
```
