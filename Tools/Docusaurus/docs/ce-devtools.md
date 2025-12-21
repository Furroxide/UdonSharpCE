# CE DevTools

CE.DevTools provides runtime debugging helpers: logging, an in-world console, and lightweight profiling.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.DevTools;

public class ProfilerExample : UdonSharpBehaviour
{
    public CEProfiler profiler;

    void Start()
    {
        profiler.StartProfiling();
    }

    void Update()
    {
        profiler.BeginSection("AI");
        // ... AI work ...
        profiler.EndSection();
    }
}
```

## API Reference

### CELogger

| Member | Description |
| --- | --- |
| `MinLevel` | Minimum log level to emit. |
| `OutputToUnityLog` | Also send logs to Unity console. |
| `Log`, `Trace`, `Debug`, `Info`, `Warning`, `Error` | Logging methods with optional tags. |
| `ClearBuffer()` / `BufferCount` | Manage internal log buffer. |

### CEDebugConsole

| Member | Description |
| --- | --- |
| `AddLog(LogEntry)` | Add a log entry to the console. |
| `Clear()` | Clear console contents. |
| `Show()` / `Hide()` / `Toggle()` | Visibility controls. |
| `SetFilter(LogLevel)` | Set minimum display level. |

### CEProfiler

| Member | Description |
| --- | --- |
| `StartProfiling()` / `StopProfiling()` / `Reset()` | Control profiling. |
| `BeginSection(string)` / `EndSection()` | Measure named sections. |
| `GetFPS()` / `GetAverageFrameTimeMs()` | Timing metrics. |
| `GetSummary()` / `LogSummary()` | Summary output. |

### PerformanceMonitor

| Member | Description |
| --- | --- |
| `GetFPS()` / `GetFrameTimeMs()` | Current FPS and frame time. |
| `GetMinFPS()` / `GetMaxFPS()` / `GetAvgFPS()` | Aggregate stats. |
| `ResetStats()` | Reset tracking. |

## Notes On CELogger Transformation

CELogger calls are transformed into `Debug.Log` calls during Udon compilation. This keeps Udon compatibility but means MinLevel filtering and in-world console integration are not applied automatically in compiled Udon code. If you want logs to appear in a `CEDebugConsole`, call `AddLog` directly or implement a custom bridge.

CEDebugConsole expects UI references to a TextMeshProUGUI or Text component and an optional ScrollRect. A prefab is available in `Packages/com.merlin.UdonSharp/Samples~/CE/DebugConsole/`.

## Common Pitfalls

### Bad

```csharp
CELogger.MinLevel = LogLevel.Warning;
CELogger.Info("This still logs in Udon-compiled output due to transformation");
```

### Good

```csharp
// Use CELogger for editor readability, or call CEDebugConsole.AddLog for in-world display.
```
