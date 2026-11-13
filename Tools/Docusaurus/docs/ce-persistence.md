# CE Persistence

CE.Persistence stores attributed models in the local player's PlayerData as JSON, with validation and size estimation.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Persistence;
using VRC.SDKBase;

[PlayerData("rpg_save")]
public class PlayerSaveData
{
    [PersistKey("xp")] public int experience;
    [PersistKey("lvl"), Range(1, 100)] public int level = 1;
}

public class SaveManager : UdonSharpBehaviour
{
    private PlayerSaveData _data;
    private bool _ready;

    public override void OnPlayerRestored(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        RestoreResult result = CEPersistence.Restore(out _data);
        if (result == RestoreResult.NoData) _data = new PlayerSaveData();
        _ready = result == RestoreResult.Success || result == RestoreResult.NoData;
    }

    public void SaveLocal()
    {
        if (_ready) CEPersistence.Save(_data);
    }
}
```

Wait for the local `OnPlayerRestored` event before treating `NoData` as a first-time player. `NotReady` means the local player is missing or invalid; it does not detect an in-progress PlayerData restore. Saving defaults before restoration can overwrite a returning player's save.

## Auto Registration

The compiler generates converters and typed overloads for `[PlayerData("key")]` models. No registration call is needed in behaviour code.

Supported fields are writable public or internal fields with `[PersistKey]`:

- Numeric primitives and `bool`.
- `string`, `DataList`, and `DataDictionary`.
- One-dimensional arrays of those types. Jagged and multidimensional arrays are unsupported.

A model can instead expose one writable public or internal `DataDictionary` field without `[PersistKey]` and build its contents itself. Use that form for complex payloads. Manual `Register<T>` converters are editor-only; using them in an Udon behaviour is a compile error. Generic models and inherited `[PersistKey]` fields are unsupported. Duplicate model/field keys and reserved `__ce_*` keys are rejected during generation.

The model class itself must be reachable and constructible from the generated code: public or internal (and so must every class it is nested in), not abstract or static, with a public or internal parameterless constructor. An internal model gets internal overloads. A model that breaks one of these rules fails the compile at its declaration; a `[PersistKey]` field of an unsupported type, or one that is private, protected, `readonly` or `const`, makes generation skip the model with a warning naming the field.

`DefaultValue` applies only together with `Optional = true`, when the key is missing from the stored data. It must be a constant the field can hold; `null` means the type's default. A value the field cannot hold, such as `300` for a `byte` or `1.5` for an `int`, is a compile error rather than a silent truncation.

Null reference fields are omitted when saving; explicit nulls are accepted when restoring reference fields. Invalid non-null field types, invalid array elements, and out-of-range numeric values return `ParseError` with corruption details. `long` and `ulong` use invariant strings to preserve all 64 bits; existing numeric saves are still accepted when representable.

## API Reference

### Model Attributes

| Attribute | Description |
| --- | --- |
| `PlayerData` | Storage key and schema version. `AutoSave` is reserved metadata; it does not schedule saves. |
| `PersistKey` | Field key, optional default and optional-field metadata. |
| `PersistIgnore` | Excludes a field. |
| `Range` | Numeric range validation. |
| `MaxLength` | String or array length validation. |
| `Required` | Requires non-empty reference values. |

### CEPersistence

The generic spellings below are rewritten to generated model-specific overloads. Type inference works when the model argument supplies the type; specify the model for a standalone `out var`, such as `Restore<PlayerSaveData>(out var data)`.

| Member | Description |
| --- | --- |
| `Save<T>(T model)` | Validate and save local PlayerData JSON; inspect the returned `SaveResult`. |
| `Restore<T>(out T model)` | Restore local PlayerData after `OnPlayerRestored`; inspect `RestoreResult`. |
| `ToData<T>(T)` / `FromData<T>(DataDictionary, out T)` | Convert model and dictionary. |
| `ToJson<T>(T, bool beautify)` / `FromJson<T>(string, out T)` | Convert model and JSON. |
| `Validate<T>(T, out List<ValidationError>)` | Validate model constraints. |
| `EstimateSize<T>(T)` | Estimate serialized size. |
| `IsRegistered<T>()`, `GetKey<T>()`, `GetVersion<T>()` | Compile-time model metadata. |
| `GetQuotaLimit()` | PlayerData storage limit from the SDK. |
| `GetUsedQuota()` | SDK-reported local storage usage. |
| `GetRemainingQuota()` | Limit minus current usage. |

### PersistenceLifecycle

Read lifecycle details in the behaviour that performed the operation. Static state belongs to each calling behaviour's program; it is not a shared cross-behaviour registry.

| Member | Description |
| --- | --- |
| `GetPendingRestoreResult()` / `GetPendingSaveResult()` | Most recent result in this behaviour. |
| `GetPendingModelKey()` | Key associated with the result. |
| `GetPendingCorruptionInfo()` | Details of the last corruption report. |

The former `RegisterCallback`, `UnregisterCallback` and `IPersistenceCallbacks` registry is removed. Forward results explicitly to another behaviour when needed.

### PlayerObjectHelper

`Initialize`, `AssignPlayerSlot`, `ReleasePlayerSlot`, `GetPlayerSlot`, `GetLocalPlayerSlot` and `GetPlayerInSlot` manage local slots. Slot numbers depend on local assignment order and can differ between clients. Use a synchronized player-ID-to-slot mapping maintained by one owner when slots index networked arrays.

## Current Limitations

- Save and restore use `PlayerData.SetString` and `PlayerData.TryGetString`.
- Missing fields retain model initializers unless an optional default is declared. `Required` checks non-empty values during validation; it does not make every missing JSON key a parse error.
- Size estimates are approximate and err high. Strings are counted in the UTF-8 bytes VRCJson writes, so non-ASCII text is not undercounted; the SDK-reported usage remains the quota authority.
- Migration between schema versions is not automatic; `VersionMismatch` needs an explicit application policy.
- Numbers are stored as VRCJson text. A `float` comes back exactly, but a `double` keeps only about 15
  significant digits (1/3 is restored as 0.333333333333333). `long` and `ulong` fields are stored as
  strings, so they round-trip exactly.
