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
| `AddLog(LogEntry)` / `AddLog(string, LogLevel)` | Add a log entry to the console. |
| `Clear()` | Clear console contents. |
| `Show()` / `Hide()` / `Toggle()` | Visibility controls. |
| `SetFilter(LogLevel)` | Set the minimum level shown. |
| `OnClearButtonClicked()` / `OnCloseButtonClicked()` | Button handlers: clear the log, hide the console. |
| `OnFilterDropdownChanged()` | Dropdown handler: applies the option selected in the Filter Dropdown (or Filter Dropdown TMP). |
| `OnAutoScrollToggleChanged()` | Toggle handler: applies the state of the Auto Scroll Toggle. |
| `OnFilterChanged(int)` / `OnAutoScrollChanged(bool)` | The same settings, for calls from code. A UI control cannot reach these. |

A uGUI control reaches the console through `UdonBehaviour.SendCustomEvent`, which passes no argument, and
U# exports a method that takes parameters under a generated name. So wire controls to the parameterless
handlers above. `OnFilterDropdownChanged` and `OnAutoScrollToggleChanged` read the value from the controls
assigned on the console, and do nothing when none is assigned.

The console keeps the last `maxLogEntries` entries of every level and applies the level filter when it
builds the text, so lowering the filter shows entries that arrived while it was raised. Entries below the
filter still take up room in the buffer. A `maxLogEntries` below 1 is treated as 1.

### CEProfiler

| Member | Description |
| --- | --- |
| `StartProfiling()` / `StopProfiling()` / `Reset()` | Control profiling. |
| `BeginSection(string)` / `EndSection()` | Measure named sections. Sections nest: each `EndSection()` closes the most recent `BeginSection()` still open, and an outer section's time includes the sections inside it. |
| `GetFPS()` / `GetAverageFrameTimeMs()` | Timing metrics. |
| `GetSummary()` / `LogSummary()` | Summary output. |

Up to 16 sections can be open at once; past that the oldest open one is dropped. At most `maxSections`
names are tracked, and a warning is logged once when that limit is reached.

Section times come from `Time.realtimeSinceStartup`, a float of seconds since the client started. Its
resolution coarsens with uptime — about 1 ms after two hours and 2 ms after four and a half — so a single
short section can read as 0 or as one whole step. Totals and averages over many calls stay usable.

### PerformanceMonitor

| Member | Description |
| --- | --- |
| `GetFPS()` / `GetFrameTimeMs()` | Current FPS and frame time. |
| `GetMinFPS()` / `GetMaxFPS()` / `GetAvgFPS()` | Aggregate stats over the readings taken every update interval. `GetMinFPS()` returns 0 until the first reading. |
| `ResetStats()` | Reset tracking. |

### Calling before Start

`CEDebugConsole` and `CEProfiler` allocate their buffers on first use, so another behaviour can call them
before their own `Start` has run — including while their GameObject is still inactive.

## Notes On CELogger Transformation

When a behaviour is compiled to Udon, a `CELogger.Trace`, `Debug`, `Info`, `Warning` or `Error` call with positional arguments, or a `CELogger.Log` call whose level is a constant (or left out, which means Info), is rewritten into the `UnityEngine.Debug` call for its level: `Debug.Log` for Trace, Debug and Info, `Debug.LogWarning` for Warning and `Debug.LogError` for Error, with a `[CE][Level]` or `[CE][tag][Level]` prefix. The severity is kept, but `MinLevel` and `OutputToUnityLog` are not consulted and the message never reaches a `CEDebugConsole`. Any other CELogger call (for example one that names an argument, a `Log` whose level is not a constant, or one made through `using static` or an alias) is left as a real call and compiled into the calling behaviour. Udon shares no memory between behaviours, so every UdonBehaviour has its own copy of CELogger's static fields: such a call is filtered by the calling behaviour's `MinLevel` and `OutputToUnityLog` (setting them in another behaviour has no effect on it), and it does not reach a console either, because a `CEDebugConsole` registers itself in its own copy. To show a message in the console, call `AddLog` on it.

CEDebugConsole expects UI references to a TextMeshProUGUI or Text component and an optional ScrollRect. The package ships no prefab for it; set one up like this:

1. Create a Canvas with a panel the console can show and hide.
2. Inside it, add a Scroll View whose content holds a TextMeshProUGUI (or legacy Text) component.
3. Add `CEDebugConsole` and assign the text component, the ScrollRect and the panel.
4. Optionally, add a Dropdown (legacy or TextMeshPro) whose options are Trace, Debug, Info, Warning and Error, in that order, and a Toggle for auto-scroll. Assign them to the console's Filter Dropdown (or Filter Dropdown TMP) and Auto Scroll Toggle fields. In each control's On Value Changed list, add an entry that targets the console's UdonBehaviour, choose `UdonBehaviour.SendCustomEvent (string)`, and enter `OnFilterDropdownChanged` or `OnAutoScrollToggleChanged`. A Button's On Click list works the same way with `OnClearButtonClicked` or `OnCloseButtonClicked`.

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
