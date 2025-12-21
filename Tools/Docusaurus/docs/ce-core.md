# CE Core

CE.Core provides low-level building blocks used across CE runtime and compile-time features, including the CECallback type and compiler-facing attributes.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Core;
using UnityEngine;

public class CallbackExample : UdonSharpBehaviour
{
    private CECallback _onDone;

    void Start()
    {
        _onDone = CECallback.Create(this, nameof(OnDone));
        _onDone.Invoke();
    }

    public void OnDone()
    {
        Debug.Log("Done");
    }
}
```

## CECallback API Reference

| Member | Description |
| --- | --- |
| `Target` | Target behaviour that owns the method to invoke. |
| `MethodName` | Method name to invoke on the target. |
| `IsValid` | True when `Target` and `MethodName` are set. |
| `Invoke()` | Invokes the callback if valid. |
| `InvokeWithLogging(string context)` | Invokes with debug logging when invalid. |
| `Create(UdonSharpBehaviour, string)` | Convenience factory for `CECallback`. |
| `None` | Invalid callback value. |

## Optimization And Compiler Attributes

These attributes are declared for CE compiler features. Only `CEPreserveAction` is currently enforced by the compiler pipeline.

| Attribute | Purpose | Status |
| --- | --- | --- |
| `CENoOptimize` | Opt out of CE optimizations on a member or type. | Reserved |
| `CENoInline` | Prevent inlining of a method. | Reserved |
| `CEInline` | Request inlining of a method. | Reserved |
| `CENoUnroll` | Prevent loop unrolling in a method. | Reserved |
| `CEUnroll` | Request loop unrolling. | Reserved |
| `CEConst` | Mark a field as a compile-time constant. | Reserved |
| `CEDebugOnly` | Remove a method in release builds. | Reserved |
| `CEPreserveAction` | Prevent Action-to-CECallback transformation. | Active |

## Notes On Action Transformation

CE converts parameterless `Action` usage to `CECallback` at compile time. This means:

- `Action<T>` and other delegate types are not supported.
- Simple lambdas like `() => Method()` are converted, but closures are not.
- Use `callback.Invoke()` (not `callback?.Invoke()`) after transformation.

## Common Pitfalls

### Bad

```csharp
using System;

public class Example : UdonSharpBehaviour
{
    private Action _onDone;

    void Start()
    {
        _onDone = () => OnDone();
        _onDone?.Invoke(); // `?.` is not valid after Action becomes CECallback
    }
}
```

### Good

```csharp
using System;

public class Example : UdonSharpBehaviour
{
    private Action _onDone;

    void Start()
    {
        _onDone = () => OnDone();
        _onDone.Invoke();
    }
}
```
