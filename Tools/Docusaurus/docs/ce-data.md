# CE Data

CE.Data provides type-safe collections and bridges to VRChat DataList/DataDictionary and JSON.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Data;
using UnityEngine;

public class InventoryExample : UdonSharpBehaviour
{
    private CEList<int> _items = new CEList<int>();
    private CEDictionary<string, int> _counts = new CEDictionary<string, int>();

    void Start()
    {
        _items.Add(10);
        _items.Add(20);

        _counts.Add("coins", 5);
        _counts["keys"] = 2;

        string json = _items.ToJson();
        Debug.Log("Items JSON: " + json);
    }
}
```

## DataToken-Compatible Types

Default conversions support:

- bool
- sbyte, byte, short, ushort, int, uint, long, ulong
- float, double
- string
- `VRC.SDK3.Data.DataList`
- `VRC.SDK3.Data.DataDictionary`

Use converter overloads for custom types.

## Limitations

- `CEList<T>` uses `T : IComparable` under Udon compilation; prefer primitives and comparable types.
- JSON deserialization yields doubles for numbers; cast explicitly in converters.

## API Reference

### CEList<T>

| Member | Description |
| --- | --- |
| `CEList()` / `CEList(int)` / `CEList(T[])` | Constructors with default capacity or source data. |
| `Count` | Number of items. |
| `this[int index]` | Indexer with bounds checks and warnings. |
| `Add`, `AddRange`, `Insert` | Add items. |
| `Remove`, `RemoveAt`, `Contains`, `IndexOf` | Remove and search. |
| `Clear`, `Reverse`, `ToArray` | Utility operations. |
| `GetUnchecked`, `SetUnchecked` | Fast access without bounds checks. |
| `GetBackingArray` | Access internal array (use with care). |
| `ForEach`, `ForEachWithIndex`, `ForEachUntil` | Allocation-free iteration helpers. |
| `ToDataList()` | Convert to `DataList` (DataToken-compatible types only). |
| `ToDataList(Func<T, DataToken>)` | Convert with custom converter. |
| `FromDataList(DataList)` | Create list from a `DataList`. |
| `FromDataList(DataList, Func<DataToken, T>)` | Create with custom converter. |
| `ToJson()` / `ToJson(bool)` | Serialize to JSON. |
| `FromJson(string)` / `FromJson(string, Func<DataToken, T>)` | Deserialize from JSON. |

### CEDictionary<TKey, TValue>

| Member | Description |
| --- | --- |
| `CEDictionary()` / `CEDictionary(int)` | Constructors. |
| `Count` | Number of entries. |
| `this[TKey key]` | Indexer with lookup and add behavior. |
| `Add`, `Remove`, `TryGetValue` | Core dictionary operations. |
| `ContainsKey`, `ContainsValue` | Search helpers. |
| `GetKeys`, `GetValues` | Snapshot arrays of keys or values. |
| `ToDataDictionary()` | Convert to `DataDictionary` (string keys recommended for JSON). |
| `ToDataDictionary(Func<TKey, DataToken>, Func<TValue, DataToken>)` | Convert with custom converters. |
| `FromDataDictionary(DataDictionary)` | Create from a `DataDictionary`. |
| `FromDataDictionary(DataDictionary, Func<DataToken, TKey>, Func<DataToken, TValue>)` | Create with converters. |
| `ToJson()` / `ToJson(bool)` | Serialize to JSON. |
| `FromJson(string)` / `FromJson(string, Func<DataToken, TKey>, Func<DataToken, TValue>)` | Deserialize from JSON. |

### CEDataBridge

| Member | Description |
| --- | --- |
| `Register<T>(Func<T, DataDictionary>, Func<DataDictionary, T>)` | Register converters for a model type. |
| `IsRegistered<T>()` | Check registration. |
| `ToData<T>(T)` / `FromData<T>(DataDictionary)` | Convert model to/from DataDictionary. |
| `ToJson<T>(T, bool beautify)` / `FromJson<T>(string)` | Convert model to/from JSON. |
| `ToDataList<T>(CEList<T>)` / `FromDataList<T>(DataList)` | Convert model lists. |
| `ListToJson<T>(CEList<T>)` / `ListFromJson<T>(string)` | Serialize model lists to JSON. |

### Data Model Attributes

| Attribute | Purpose |
| --- | --- |
| `DataModel` | Marks a class as a data model (manual registration required). |
| `DataField` | Specifies the serialized key for a field. |
| `DataIgnore` | Excludes a field from serialization. |

## Common Pitfalls

### Bad

```csharp
var list = new CEList<Vector3>();
var data = list.ToDataList(); // Vector3 is not DataToken-compatible
```

### Good

```csharp
using UdonSharp.CE.Data;
using VRC.SDK3.Data;

var list = new CEList<Vector3>();
var data = list.ToDataList(v =>
{
    var dict = new DataDictionary();
    dict["x"] = v.x;
    dict["y"] = v.y;
    dict["z"] = v.z;
    return new DataToken(dict);
});
```
