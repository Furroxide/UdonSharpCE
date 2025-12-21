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
        CEPersistence.Register<PlayerSaveData>(
            toData: data =>
            {
                var dict = new DataDictionary();
                dict["xp"] = data.experience;
                dict["lvl"] = data.level;
                return dict;
            },
            fromData: dict => new PlayerSaveData
            {
                experience = (int)dict["xp"].Double,
                level = (int)dict["lvl"].Double
            },
            key: "rpg_save",
            version: 1
        );
    }

    public void SaveLocal()
    {
        CEPersistence.Save(_data);
    }
}
```

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
| `Register<T>(toData, fromData, key, version, validate)` | Register a data model and converters. |
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
- You must register model converters manually via `Register<T>`.
- JSON numbers deserialize as doubles; cast carefully in `fromData`.

## Common Pitfalls

### Bad

```csharp
// Missing registration causes Save/Restore to fail.
CEPersistence.Save(_data);
```

### Good

```csharp
if (!CEPersistence.IsRegistered<PlayerSaveData>())
{
    // Register converters in Start() before saving.
}
```
