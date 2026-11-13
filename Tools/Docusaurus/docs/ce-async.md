# CE Async

CE.Async lets you write time-based and frame-based sequences as straight-line C# instead of a hand-rolled
state machine. During Udon compilation CE rewrites each `async UdonTask` method into a switch-based state
machine that resumes itself with `SendCustomEventDelayedSeconds` / `SendCustomEventDelayedFrames`.

**`UdonTask` is fire-and-forget.** It is a struct with nowhere to register a continuation, so the value a
caller receives is a snapshot of the moment the call returned and never changes afterwards. Only an `await`
inside another `async UdonTask` method observes completion, because CE wires that continuation up at
compile time. Awaiting a `UdonTask` from anywhere CE did not lower is a compile error (CE0024), not an
await that silently resumes straight away.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Async;
using UnityEngine;

public class CutsceneStep : UdonSharpBehaviour
{
    public void OnPlay()
    {
        Play();   // fire-and-forget: the sequence runs, the return value is not a handle
    }

    public async UdonTask Play()
    {
        Debug.Log("Fade out");
        await UdonTask.Delay(1.0f);

        Debug.Log("Wait one frame");
        await UdonTask.Yield();

        Debug.Log("Continue");
        await FadeIn();       // chaining another async method on this behaviour is supported
    }

    private async UdonTask FadeIn()
    {
        await UdonTask.Yield();
        Debug.Log("Faded in");
    }
}
```

Keep the `async` keyword on methods that contain `await` — Unity still compiles the same file into
Assembly-CSharp as ordinary C#. CE strips `async` during the Udon compilation step.

## What CE Generates

For an async method `Foo` on a behaviour, CE replaces the method body with an entry point and appends these
members to the class:

| Member | Role |
| --- | --- |
| `__Foo_state` | Which switch case runs on the next resume. |
| `__Foo_continuation` | Event name to raise when the sequence finishes; empty for a fire-and-forget start. |
| `__Foo_pendingContinuation` | Holds the caller's continuation while `__Foo_Start` runs the first segment. |
| `__Foo_running` | True between start and completion. Drives the re-entrancy guard below. |
| `__Foo_<local>` | One field per parameter and per hoisted local (see [Locals](#locals)). |
| `__Foo_Start(..., string continuation)` | Seeds the fields and runs the first segment. Returns `true` when its caller should carry on by itself: the body finished without suspending, or the call was refused. |
| `__Foo_MoveNext()` | The state machine. Public because delayed events resume it by name. |
| `__Foo_Complete()` | Clears `__Foo_running` and raises the continuation, if any. |

These are real members of your behaviour and real variables in the resulting Udon program. A member of
your own that already uses one of those names is CE0025 at the async method, not a duplicate definition
inside generated code. A hoisted local that would collide, such as one named `state`, gets a numbered
field instead.

The entry point is your own declaration with its body replaced: attributes, doc comment, modifiers and
parameter defaults all stay, so the passes that run after this one still see them.

Resumes are scheduled at `EventTiming.PostLateUpdate`.

The entry point returns a snapshot: `UdonTask.Started` when the machine suspended on an await, or
`UdonTask.CompletedTask` when the whole body ran to completion synchronously. A refused call (below) also
returns `UdonTask.Started`, because the earlier call is still running.

### How an awaited child hands back

When the child finishes without suspending — every path through it ended before reaching an await — its
`__Child_Start` returns `true` and the parent carries straight on, inside the same event. A child that does
suspend resumes its parent with `SendCustomEvent` once it finishes. So a loop that awaits a child with
nothing to wait for raises no events at all, however many times it runs. Raising one per iteration would
nest them inside each other, and Udon halts a behaviour once events nest 1000 deep.

### One call at a time

The machine's locals live in shared instance fields, so two overlapping calls to the same async method on
the same behaviour would overwrite each other. `__Foo_Start` refuses the second call, logs an error naming
the method, and lets the first one finish. If the refused call was an `await` from another async method,
that caller is resumed immediately so it does not wait forever on a sequence that never started — which
means its next statement runs before the work it awaited is done.

If you need overlapping sequences, guard the call yourself or move the work onto a second behaviour
instance. There is no per-call storage in Udon to fix this properly.

## What CE Can Lower

CE lowers a method only when **all** of these hold:

- It is declared `async`.
- It returns plain `UdonTask` (not `UdonTask<T>` — see CE0022). An alias such as
  `using Wait = UdonSharp.CE.Async.UdonTask;` counts.
- It has a braced body. An expression-bodied async method is CE0024.
- It contains at least one `await` (see CE0023).
- It is an instance method, not generic, declared directly in a class that derives from
  `UdonSharpBehaviour` — not in a struct, not on a plain C# class.
- It takes no `ref`, `out` or `in` parameter. C# forbids those on an async method as well.
- No other `async UdonTask` method on the class or on a base class has the same name: no overloads, and no
  override of an async base method. Overriding a virtual method that is not async is fine.

A method that breaks one of the last three rules is CE0025, reported at the method.

Every `await` must be a statement of its own — `await Something();` — and its target must be one of:

| Await target | Lowers to |
| --- | --- |
| `await UdonTask.Delay(seconds)` | `SendCustomEventDelayedSeconds("__Foo_MoveNext", seconds, PostLateUpdate)` |
| `await UdonTask.DelayFrames(frames)` | `SendCustomEventDelayedFrames("__Foo_MoveNext", frames, PostLateUpdate)` |
| `await UdonTask.Yield()` | `SendCustomEventDelayedFrames("__Foo_MoveNext", 1, PostLateUpdate)` |
| `await OtherAsyncMethod(args)` | `if (__OtherAsyncMethod_Start(args, "__Foo_MoveNext")) __Foo_MoveNext();` |

Targets are recognised through the compiler's semantic model, not by spelling: a fully qualified or aliased
`UdonTask` works, and a type of your own whose name merely ends in `UdonTask` is not taken for it (CE0025).

`OtherAsyncMethod` must be an `async UdonTask` method declared on the **same** class — any part of a partial
class — called with no receiver or with `this.`. One inherited from a base class, or one on another
behaviour, cannot be awaited (CE0025): its generated `__Name_Start` is private to the class that declares
it, and the continuation is delivered by `SendCustomEvent` on this behaviour.

Arguments reach the parameters they name, so `await Fade(to: 1f, from: 0f)` passes each value where it
belongs. They are evaluated in parameter order, which is how U# evaluates named arguments everywhere. Every
parameter has to be passed: leaving an optional parameter to its default, or listing `params` elements one
by one, is CE0025. Write the value out, or pass an array (`new[] { a, b }`).

An await statement may appear in exactly three places:

1. Directly in the method body.
2. Directly in the body of a `while` loop that is itself a statement of the method body. The loop may
   contain exactly one await, and statements before and after it are preserved across the suspension.
   `break` and `continue` in the loop body work as written, before or after the await; a `break` inside a
   nested `switch`, and either one inside a nested loop, still belongs to that statement.
3. As an early exit of the form `if (condition) { await ...; return; }`, at method-body level, with no
   `else`.

Anything else — an await inside `for`, `foreach`, `do`, `switch`, `try`, `using`, `lock`, an `if/else`, a
nested block, a condition or other expression, a second await in the same `while` body, a lambda, or a local
function — is CE0025 or CE0024, reported at your line. To wait twice per iteration, move both waits into
an async helper on the same class and await that once. Nothing is silently dropped.

### Locals

Parameters, locals declared directly in the method body, and locals declared directly in an awaiting
`while` body are hoisted into instance fields so their values survive the return from `MoveNext`. `var` is
supported; CE infers the type from the semantic model and writes it out. If the type cannot be inferred you
get CE0026 and write the type yourself.

At those same levels an out variable is hoisted too, as in `if (!int.TryParse(text, out int n)) return;`.
An array initializer (`int[] steps = { 1, 2 };`) is kept. A `const` local becomes a `const` field, so it
still works as a `case` label. `nameof(local)` keeps the name you wrote. A `using` declaration hoists like
any local: UdonSharp compiles one as a plain local and never calls `Dispose`, in an async method or not.

Locals declared inside a nested block that CE emits whole — the body of a non-awaiting `if`, `for`, or
`foreach` — keep their normal scope and are not hoisted.

Two locals with the same name in different scopes get separate fields. A local named `x` never affects
`vec.x`: hoisting is resolved through the semantic model, not by matching names.

### Preprocessor directives

An `#if` inside a statement that CE moves into the state machine is decided the way the rest of your file
was, with the compile's own symbols (`UNITY_EDITOR`, `COMPILER_UDONSHARP` and the rest), so code fenced off
from Udon stays out of it.

### Syntax UdonSharp cannot compile

UdonSharp refuses some C# in every method: local functions, `try`/`catch`/`finally`, `throw`, `goto` and
labels, `lock`, `using` statements, `checked`/`unchecked` blocks, tuples and deconstruction, `is` (type
tests and patterns alike), `?.`, `switch` expressions, `ref` expressions, anonymous types, query syntax,
`typeof` of your own types, collection and nested initializers, and multidimensional arrays. Inside an
async method, CE moves your code into a generated state machine, so UdonSharp's own error for any of these
would point at generated code. CE reports it first instead, as CE0025 at your line, in UdonSharp's words.
`await foreach` and `await using` are CE0025 as well: their await is not one CE can lower.

The check follows UdonSharp, so it reports only what UdonSharp would actually compile. It skips the branch
of an `if` whose condition UdonSharp folds to a constant, lambda bodies, pattern `case` labels, and the
right side of `??` over a value type. Code in those places compiles today, and it still does.

## Diagnostics

| Code | Meaning |
| --- | --- |
| CE0022 | `async UdonTask<T>` — the machine returns to its caller at the first await, so it has no point at which to hand a result back. Return `UdonTask` and store the result in a field. |
| CE0023 | `async` with no `await` — CE has nothing to lower and the C# compiler would build a state machine Udon cannot run. Drop `async` and `return UdonTask.CompletedTask;`. |
| CE0024 | An `await` CE owns but did not lower: one that awaits a `UdonTask`, one in a method or lambda that returns `UdonTask`, or any await in an `UdonSharpBehaviour` outside an `async UdonTask` method with a braced body. Move it into such a method, or call without awaiting. |
| CE0025 | An await position, await target, argument list, local or method declaration CE does not support (see [What CE Can Lower](#what-ce-can-lower)), or [syntax UdonSharp cannot compile](#syntax-udonsharp-cannot-compile) inside an async method. The message names the cause. |
| CE0026 | A `var` local whose type CE cannot infer for hoisting. Write the explicit type. |

All of these fail the compile and point at your own source line, never into generated code. The async
transform is a code generator, not an optimization: a method it owns is either lowered correctly or
refused, never degraded to a warning. Code it does not own is left exactly as written — an ordinary
`async Task` method on a class that is not an `UdonSharpBehaviour`, for instance, which Unity compiles into
Assembly-CSharp alongside your behaviours.

## API Reference

### UdonTask

| Member | Description |
| --- | --- |
| `Status` | Current `TaskStatus`. |
| `IsCompleted` | True when finished, canceled, or faulted. |
| `IsCompletedSuccessfully` | True only when completed successfully. |
| `IsCanceled` | True when canceled. |
| `IsFaulted` | True when faulted. |
| `Error` | Error message if faulted. |
| `CompletedTask` | A completed task. |
| `Started` | A task that has started and not finished. Returned by a generated entry point that suspended. |
| `Delay(float seconds)` | Awaitable time delay. |
| `DelayFrames(int frames)` | Awaitable frame delay. |
| `Yield()` | Awaitable single-frame delay. |
| `WhenAll(params UdonTask[])` | Combines already-known statuses. **Not awaitable** — `await UdonTask.WhenAll(...)` is CE0025. |
| `WhenAny(params UdonTask[])` | Combines already-known statuses. **Not awaitable** — `await UdonTask.WhenAny(...)` is CE0025. |
| `FromCanceled()` | Create a canceled task. |
| `FromError(string)` | Create a faulted task. |
| `FromException(Exception)` | Create a faulted task from an exception. |
| `GetAwaiter()` | Awaiter support for the Assembly-CSharp compilation. Never runs in a world. |

Because a `UdonTask` value never updates, `WhenAll` / `WhenAny` can only report on statuses that were
already final when you called them. They are useful for inspecting stored task values, not for waiting.

### UdonTask&lt;T&gt;

`async UdonTask<T>` is a compile error (CE0022). The type itself is still usable for values you already
have.

| Member | Description |
| --- | --- |
| `Result` | Result value; logs a warning if the task did not complete successfully. |
| `Status` / `IsCompleted` | Same as `UdonTask`. |
| `FromResult(T)` | Create a completed task with a result. |
| `FromCanceled()` / `FromError(string)` / `FromException(Exception)` | Create canceled or faulted tasks. |
| `GetAwaiter()` | Awaiter support for the Assembly-CSharp compilation. Never runs in a world. |

### Cancellation

Cancellation is cooperative and is **not** wired into the state machine. Setting a token does not stop a
running sequence; your code has to check it and `return`.

| Type | Member | Description |
| --- | --- | --- |
| `CancellationToken` | `IsCancellationRequested` | True when cancellation was requested. |
| `CancellationToken` | `ThrowIfCancellationRequested()` | Logs a warning if canceled. Does not throw. |
| `CancellationTokenSource` | `Token` | Token associated with the source. |
| `CancellationTokenSource` | `Cancel()` | Request cancellation. |
| `CancellationTokenSource` | `Reset()` | Clear the canceled state. |

### TaskStatus

| Value | Meaning |
| --- | --- |
| `Created` | Initialized but not scheduled. |
| `WaitingForActivation` | Awaiting scheduling. |
| `WaitingToRun` | Scheduled but not running. |
| `Running` | In progress. |
| `RanToCompletion` | Completed successfully. |
| `Canceled` | Canceled (cooperative). |
| `Faulted` | Faulted with an error. |

## Limitations

- Fire-and-forget only: a caller outside an async method cannot observe completion. Signal completion from
  the end of the async method itself.
- One in-flight call per async method per behaviour; the second is refused and logged, and an awaiting
  caller of the refused call resumes early.
- `async UdonTask<T>`, `async` lambdas, `async` local functions, `yield return`, and `await` inside `try`
  are not supported and are compile errors.
- `await` inside `for`, `foreach`, `do`, `switch`, `if/else`, or a nested block is not supported, and a
  `while` body holds one await. Only the three positions listed above are.
- Static, generic and overloaded async methods, and `ref` / `out` / `in` parameters, are not supported.
- An awaited child must be declared on the same class, and every one of its arguments written out.
- Syntax UdonSharp cannot compile in any method is CE0025 inside an async method, at your line.
- `WhenAll` / `WhenAny` are not awaitable.
- Cancellation is cooperative; check `token.IsCancellationRequested` yourself.
- There is no exception handling: Udon has no `try`/`catch` at runtime, and a faulted `UdonTask` is only a
  status you can read.

## Common Pitfalls

### Returning a value

```csharp
// Bad -- CE0022: an async method has no point at which to hand a result back.
public async UdonTask<int> CalculateAsync(int input)
{
    await UdonTask.Delay(1f);
    return input * 2;
}
```

```csharp
// Good -- store the result in a field, and read it after the await that chained this method.
private int _lastResult;

public async UdonTask CalculateAsync(int input)
{
    await UdonTask.Delay(1f);
    _lastResult = input * 2;
}

public async UdonTask UseResult()
{
    await CalculateAsync(5);
    Debug.Log(_lastResult);   // 10
}
```

### Awaiting from non-async code

```csharp
// Bad -- CE0024: Interact is not an async UdonTask method, so this await cannot be lowered.
public override void Interact()
{
    await PlayCutsceneAsync();
}
```

```csharp
// Good -- start it and let it run; have the sequence announce its own end.
public override void Interact()
{
    PlayCutsceneAsync();
}

public async UdonTask PlayCutsceneAsync()
{
    await UdonTask.Delay(2f);
    OnCutsceneFinished();
}
```

### Awaiting in an unsupported position

```csharp
// Bad -- CE0025: the await is inside a for loop.
for (int i = 0; i < 10; i++)
{
    await UdonTask.Yield();
}
```

```csharp
// Good -- a while loop at method-body level is the supported looping form.
int i = 0;
while (i < 10)
{
    await UdonTask.Yield();
    i++;
}
```
