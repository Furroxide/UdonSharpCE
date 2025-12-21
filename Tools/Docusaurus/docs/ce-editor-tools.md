# CE Editor Tools

CE Editor tools provide compile-time analyzers, code optimizers, a custom inspector, and diagnostics windows to catch issues early.

## Quick Start

- Open the optimization report: `Udon CE/Optimization Report`
- Analyze bandwidth: `Udon CE/Dev Tools/Bandwidth Analyzer`
- Validate a scene: `Udon CE/Dev Tools/World Validator`

## Compile-Time Analyzers

| ID | Area | Description |
| --- | --- | --- |
| CE0001 | Sync | Error on uninitialized synced arrays. |
| CE0002 | Performance | Warn on `GetComponent` in Update/FixedUpdate/LateUpdate. |
| CE0003 | Sync | Warn when continuous sync payload exceeds ~200 bytes. |
| CE0010 | Net | Validate `[Sync]` usage (quantize, delta, interpolation). |
| CE0011 | Net | Validate `[Rpc]` methods (parameters, sync mode, rate limits). |
| CE0012 | Net | Error on network calls to `[LocalOnly]` methods. |
| CE0020 | Async | Validate `UdonTask` methods and unsupported patterns. |
| CE0030 | Persistence | Estimate PlayerData size and warn near 100KB quota. |

## Compile-Time Optimizers

| Optimizer | Purpose |
| --- | --- |
| Async Method Transformer | Rewrites `async UdonTask` methods into state machines. |
| Action To CECallback | Converts parameterless `Action` to `CECallback`. |
| CELogger Transform | Rewrites `CELogger` calls into `Debug.Log` calls. |
| Loop Invariant Code Motion | Hoists loop-invariant expressions. |
| Small Loop Unrolling | Unrolls tiny fixed loops. |
| Common Subexpression Elimination | Reuses repeated expressions. |
| Tiny Method Inlining | Inlines small private methods. |
| Extern Call Caching | Caches repeated extern property access. |
| String Interning | Deduplicates string literals. |

## CE Inspector

The CE Inspector enhances UdonSharpBehaviour inspectors with:

- Property grouping and quick status badges.
- Optimization summaries from the last compile.
- Preferences under `Edit > Preferences > UdonSharp CE > Inspector`.

Menu shortcuts:

- `Udon CE/Inspector/Toggle Optimization Info`
- `Udon CE/Inspector/Toggle Property Grouping`
- `Udon CE/Inspector/Preferences`

## Dev Tools Windows

| Menu Path | Purpose |
| --- | --- |
| `Udon CE/Dev Tools/Bandwidth Analyzer` | Estimate sync payload and bandwidth usage. |
| `Udon CE/Dev Tools/World Validator` | Run scene-wide checks and recommendations. |
| `Udon CE/Dev Tools/Network Simulator` | Simulate lag, loss, and jitter. |
| `Udon CE/Dev Tools/Late-Join Simulator` | Simulate late-join sync behavior. |

## Limitations

- Optimization attributes such as `[CEInline]` and `[CENoOptimize]` are declared but not enforced yet.
- Async transformation supports `Delay`, `DelayFrames`, and `Yield` only. Awaiting task results is not supported.
- `CELogger` calls are transformed to `Debug.Log` for Udon compatibility; filtering is bypassed.

## Common Pitfalls

### Bad

```csharp
[CEInline]
private float Square(float x) => x * x; // Attribute is not enforced yet
```

### Good

```csharp
private float Square(float x) => x * x; // Let the optimizer decide
```
