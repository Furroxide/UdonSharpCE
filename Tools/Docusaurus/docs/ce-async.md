# CE Async

CE.Async introduces `UdonTask` and cooperative cancellation helpers for time-based and frame-based workflows.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Async;
using UnityEngine;

public class CutsceneStep : UdonSharpBehaviour
{
    public async UdonTask Play()
    {
        Debug.Log("Fade out");
        await UdonTask.Delay(1.0f);

        Debug.Log("Wait one frame");
        await UdonTask.Yield();

        Debug.Log("Continue");
    }
}
```

## How It Works

During Udon compilation, CE rewrites `async UdonTask` methods into a state machine that schedules continuation points on future frames or time delays. There is no multithreading; all work remains on the main thread.

For C# compilation, keep the `async` keyword on methods that contain `await`. CE strips it during the Udon compilation step.

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
| `CompletedTask` | Completed task instance. |
| `Delay(float seconds)` | Await a time delay. |
| `DelayFrames(int frames)` | Await a frame delay. |
| `Yield()` | Await the next frame. |
| `WhenAll(params UdonTask[])` | Returns a task that represents all tasks (not yet awaited by the transformer). |
| `WhenAny(params UdonTask[])` | Returns a task that represents any task (not yet awaited by the transformer). |
| `FromCanceled()` | Create a canceled task. |
| `FromError(string)` | Create a faulted task. |
| `FromException(Exception)` | Create a faulted task from an exception. |
| `GetAwaiter()` | Awaiter support for the C# compiler. |

### UdonTask<T>

| Member | Description |
| --- | --- |
| `Result` | Result value after completion (see limitations below). |
| `Status` / `IsCompleted` | Same as `UdonTask`. |
| `FromResult(T)` | Create a completed task with a result. |
| `FromCanceled()` / `FromError()` | Create canceled or faulted tasks. |
| `GetAwaiter()` | Awaiter support for the C# compiler. |

### Cancellation

| Type | Member | Description |
| --- | --- | --- |
| `CancellationToken` | `IsCancellationRequested` | True when cancellation was requested. |
| `CancellationToken` | `ThrowIfCancellationRequested()` | Logs a warning if canceled. |
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

## Current Limitations

- Only `await UdonTask.Delay`, `DelayFrames`, and `Yield` are transformed into proper waits.
- Awaiting tasks that return values is not supported; `UdonTask<T>` results are not wired in the transformer yet.
- `WhenAll` and `WhenAny` return placeholder tasks; the transformer does not wait on them.
- `async` lambdas, `yield return`, and `try/finally` that contains `await` are not supported.
- Cancellation is cooperative; you must check `token.IsCancellationRequested` yourself.

## Common Pitfalls

### Bad

```csharp
public async UdonTask<int> CalculateAsync(int input)
{
    await UdonTask.Delay(1f);
    return input * 2; // Result assignment is not wired in the transformer yet
}
```

### Good

```csharp
private int _lastResult;

public async UdonTask CalculateAsync(int input)
{
    await UdonTask.Delay(1f);
    _lastResult = input * 2; // Store results in fields or out params for now
}
```
