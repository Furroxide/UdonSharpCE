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

`Invoke()` goes through `SendCustomEvent`, so `MethodName` has to be a name U# exports from the target's program: a public parameterless instance method under its own name, or a built-in Udon event under its Udon name (`Interact` is `"_interact"`, `Start` is `"_start"`). Any other name compiles and then never fires.

## Optimization And Compiler Attributes

`CENoOptimize`, `CENoInline`, `CEInline`, `CENoUnroll`, `CEUnroll`, `CEConst` and `CEDebugOnly` are declared `[Obsolete(error: true)]`, so using one is a compile error. The optimizer passes they controlled are disabled, which leaves them nothing to opt into or out of. `CEPreserveAction` is the only attribute in this group that the compiler reads.

| Attribute | Purpose | Status |
| --- | --- | --- |
| `CENoOptimize` | Opt out of CE optimizations on a member or type. | `[Obsolete(error: true)]` |
| `CENoInline` | Prevent inlining of a method. | `[Obsolete(error: true)]` |
| `CEInline` | Request inlining of a method. | `[Obsolete(error: true)]` |
| `CENoUnroll` | Prevent loop unrolling in a method. | `[Obsolete(error: true)]` |
| `CEUnroll` | Request loop unrolling. | `[Obsolete(error: true)]` |
| `CEConst` | Mark a field as a compile-time constant. | `[Obsolete(error: true)]` |
| `CEDebugOnly` | Remove a method in release builds. | `[Obsolete(error: true)]` |
| `CEPreserveAction` | Keep `System.Action` and lambdas in a type or method exactly as written. | Active |

## Action To CECallback

Udon has no delegates, so CE rewrites parameterless `System.Action` into `CECallback` at compile time.

What is rewritten:

- `Action` as the type of a field, local, parameter, property or method return value, including as a type argument (`List<Action>`).
- `() => Method()`, `() => this.Method()`, `() => { Method(); }` and `delegate { Method(); }`, where `Method` is a parameterless method of the same behaviour, become `new CECallback(this, "Method")`.
- `new Action(Method)` becomes `new CECallback(this, "Method")`.

The string is the name U# exports the method under: the written name for a public method, and the Udon event name for a built-in event, whatever its accessibility (`() => Interact()` becomes `"_interact"`). Call a converted value with `callback.Invoke()`.

What keeps a file exactly as written, so that U# reports the delegate itself:

- `Action<T>`, `Func<T>` and other delegate types. They are never converted.
- A lambda that captures a local, takes parameters, does more than make one call, or calls a method on another object.
- A method group anywhere except inside `new Action(Method)`.
- Any use of an `Action` value other than storing, passing, returning and calling `.Invoke()`: `?.Invoke()`, `handler()`, `+=` and `-=`, comparing with `null`, `ReferenceEquals`, converting to `object` or `Delegate`, members such as `.Method`, `typeof(Action)`, casts, `null` and `default`, `new Action[n]`, `foreach (var handler in handlers)` and `event Action`.
- Handing an `Action` to an API that is not U# source (for example `Task.Run`), or to code marked `[CEPreserveAction]`.
- A callback-shaped lambda outside an `UdonSharpBehaviour`, or in a static member, because a callback needs a behaviour instance to raise the event on.

Files that share an `Action` member are converted together or not at all. That covers a declaration and every file that uses it, an override and the member it overrides, and an interface member and its implementation. One file CE cannot convert keeps all the others it shares a member with as written.

`CE0062` is a compile error for a lambda or `new Action(...)` that is the right shape but names a target `SendCustomEvent` cannot reach: a private or static method that is not a built-in event, a method with parameters, a local function, a delegate value, or a method the behaviour inherits from `UdonSharpBehaviour` without overriding it (`RequestSerialization()`, or `Interact()` when the behaviour has no `Interact` of its own). It is reported only when that target is what keeps the file from being converted.

## Lambda Inlining

CE inlines a lambda into a plain loop when the lambda is the only argument of one of these calls, written as a statement:

- `System.Collections.Generic.List<T>.ForEach`
- `CEList<T>.ForEach`, `CEList<T>.ForEachWithIndex` and `CEList<T>.ForEachUntil`

The argument can also be a method group that names a method of the same object (`list.ForEach(Process)` or `list.ForEach(this.Process)`) or a static method.

In the loop the lambda's parameters keep their names, `return;` moves on to the next element just as it does in the lambda, and a nested `ForEach` call is inlined as well. The call is left as written, and reported by `CE0021`, when:

- the lambda returns from inside a loop of its own, or uses a label;
- one of its parameters, or a local it declares, has the name of a local or parameter of the enclosing method;
- a `ForEachUntil` lambda is more than an expression or a single `return`;
- the lambda is `async`, has `ref`, `out` or `in` parameters, or its body is a `throw` expression;
- the call is not a statement (for example the body of an expression-bodied member), contains a `#if` directive, or sits inside a `[CEPreserveAction]` type or method;
- the receiver is any other type with a `ForEach` method, such as `CEQuery`, `CEPool` or an extension method.

Immediately invoked lambdas such as `(() => { ... })()` are not inlined. Write the loop out explicitly, or move the body into a public method and use a `CECallback`.

## Common Pitfalls

### Bad

```csharp
using System;
using UdonSharp;

public class Example : UdonSharpBehaviour
{
    private Action _onDone;

    void Start()
    {
        _onDone = () => Finish();
        _onDone?.Invoke(); // CE cannot follow `?.`, so this file keeps System.Action and U# rejects the delegate
    }

    private void Finish() { } // CE0062 once `?.` is gone: a private method is not exported under its name
}
```

### Good

```csharp
using System;
using UdonSharp;

public class Example : UdonSharpBehaviour
{
    private Action _onDone;

    void Start()
    {
        _onDone = () => Finish();
        _onDone.Invoke();
    }

    public void Finish() { }
}
```
