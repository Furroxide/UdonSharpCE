# CE Persistence

CE.Persistence provides a model-based layer for persistent player data, validation, and size estimation.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Persistence;
using VRC.SDK3.Data;

[PlayerData("rpg_save")]
public class PlayerSaveData
{
    [PersistKey("xp")] public int experience;
    [PersistKey("lvl"), Range(1, 100)] public int level = 1;
}

public class SaveManager : UdonSharpBehaviour
{
    private PlayerSaveData _data = new PlayerSaveData();

    void Start()
    {
        // No manual registration required.
    }

    public void SaveLocal()
    {
        CEPersistence.Save(_data);
    }
}
```

## Auto Registration

CE.Persistence auto-registers `[PlayerData]` models during Udon compilation. You only need the attributes.

Supported field types:

- Primitives (`bool`, `int`, `float`, etc.)
- `string`
- `DataList` / `DataDictionary`
- Arrays of the above

If you need custom conversion (complex types, nested models, etc.), register a converter manually with `CEPersistence.Register`.

## API Reference

### Model Attributes

| Attribute | Description |
| --- | --- |
| `PlayerData` | Marks a class as persistent data with a storage key. |
| `PersistKey` | Defines the key name used for a field. |
| `PersistIgnore` | Excludes a field from persistence. |
| `Range` | Numeric range validation. |
| `MaxLength` | String or array length validation. |
| `Required` | Requires non-empty values. |

### CEPersistence

| Member | Description |
| --- | --- |
| `Register<T>(toData, fromData, key, version, validate)` | Register a data model and converters (optional; only for custom converters). |
| `IsRegistered<T>()` | Check if a model is registered. |
| `Save<T>(T model)` | Save a model (placeholder flow; see limitations). |
| `Restore<T>(out T model)` | Restore a model (placeholder flow; see limitations). |
| `ToData<T>(T model)` / `FromData<T>(DataDictionary, out T)` | Convert to and from DataDictionary. |
| `ToJson<T>(T model, bool beautify)` / `FromJson<T>(string, out T)` | Convert to and from JSON. |
| `Validate<T>(T model, out List<ValidationError>)` | Validate a model against constraints. |
| `EstimateSize<T>(T model)` | Estimate serialized size in bytes. |
| `GetRemainingQuota()` | Placeholder; returns full quota for now. |

### PersistenceLifecycle

| Member | Description |
| --- | --- |
| `RegisterCallback(UdonSharpBehaviour)` | Register for save/restore lifecycle events. |
| `UnregisterCallback(UdonSharpBehaviour)` | Remove a callback. |
| `GetPendingRestoreResult()` / `GetPendingSaveResult()` | Access last result inside callbacks. |
| `GetPendingModelKey()` | Access model key inside callbacks. |
| `GetPendingCorruptionInfo()` | Access corruption details inside callbacks. |

Callbacks are invoked by name. Implement `_CE_OnDataRestored`, `_CE_OnDataSaved`, and `_CE_OnDataCorrupted` on your behaviour and read the pending data via the accessor methods above.

### PlayerObjectHelper

| Member | Description |
| --- | --- |
| `Initialize()` | Initialize slot tracking. |
| `AssignPlayerSlot(VRCPlayerApi)` | Assign a player to a slot. |
| `ReleasePlayerSlot(VRCPlayerApi)` | Release a slot. |
| `GetPlayerSlot(VRCPlayerApi)` / `GetLocalPlayerSlot()` | Retrieve slot IDs. |
| `GetPlayerInSlot(int)` | Retrieve player by slot. |

## Current Limitations

- `Save` and `Restore` are placeholders for future PlayerData integration.
- Auto-registration runs during Udon compilation and supports primitives, strings, DataList/DataDictionary, and arrays of those types.
- Unsupported field types require manual converters via `Register<T>`.
- JSON numbers deserialize as doubles; cast carefully in custom converters.

## Common Pitfalls

### Bad

```csharp
// Using unsupported field types without a custom converter.
CEPersistence.Save(_data);
```

### Good

```csharp
// Keep persistence fields to supported types, or register a custom converter.
```
