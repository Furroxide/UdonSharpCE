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

Use converter overloads for custom types (editor / plain C# only, see below).

## Numbers And JSON

VRCJson returns every JSON number as a `Double`. The non-converter read paths (`FromJson(string)`, `FromDataList(DataList)`, `FromDataDictionary(DataDictionary)`) convert a numeric token of **any** numeric type to the element type you asked for, so `CEList<int>.FromJson("[1,2,3]")` is `[1, 2, 3]` and a `CEDictionary<string, int>` survives a `ToJson` / `FromJson` round trip.

- Integer targets truncate toward zero (`2.7` becomes `2`) and clamp to the target's range, so an oversized number becomes `int.MaxValue` rather than halting the behaviour.
- A token whose type already matches is returned as is, so a `long` keeps its exact value.
- A boolean is not a number: read as an `int` it is `0`.
- A token of the wrong kind altogether (a string where a number was expected) becomes the default value.

## Limitations

- `CEList<T>` uses `T : IComparable` under Udon compilation; prefer primitives and comparable types. `Vector3`, for one, is not comparable, so it cannot be a CEList element in an UdonSharpBehaviour.
- Udon has no delegates, and CE inlines a lambda only where it is passed to `CEList`'s `ForEach`, `ForEachWithIndex` or `ForEachUntil` (or to `List<T>.ForEach`) in a statement of its own. Every overload below that takes a `Func` converter, and all of `CEDataBridge`, can be called from editor and plain C# code but not from an UdonSharpBehaviour.
- JSON object keys are strings, so only `CEDictionary<string, TValue>` round-trips through `ToJson` / `FromJson`.

## API Reference

### CEList&lt;T&gt;

| Member | Description |
| --- | --- |
| `CEList()` / `CEList(int)` / `CEList(T[])` | Constructors with default capacity or source data. |
| `Count` | Number of items. |
| `this[int index]` | Indexer. An out-of-range index is fatal: it logs an error and then deliberately faults, which halts the calling behaviour under Udon. Check `Count` first. The same applies to `Insert` and `RemoveAt`. |
| `Add`, `AddRange`, `Insert` | Add items. |
| `Remove`, `RemoveAt`, `Contains`, `IndexOf` | Remove and search. |
| `Clear`, `Reverse`, `ToArray` | Utility operations. |
| `GetUnchecked`, `SetUnchecked` | Fast access without bounds checks. |
| `GetBackingArray` | Access internal array (use with care). |
| `ForEach`, `ForEachWithIndex`, `ForEachUntil` | Allocation-free iteration helpers. Lambdas passed here are inlined, so these work in Udon. |
| `ToDataList()` | Convert to `DataList` (DataToken-compatible types only). |
| `ToDataList(Func<T, DataToken>)` | Convert with custom converter. Editor / plain C# only. |
| `FromDataList(DataList)` | Create list from a `DataList`. |
| `FromDataList(DataList, Func<DataToken, T>)` | Create with custom converter. Editor / plain C# only. |
| `ToJson()` / `ToJson(bool)` | Serialize to JSON. |
| `FromJson(string)` | Deserialize from JSON; numbers are converted to `T`. |
| `FromJson(string, Func<DataToken, T>)` | Deserialize with custom converter. Editor / plain C# only. |

### CEDictionary&lt;TKey, TValue&gt;

| Member | Description |
| --- | --- |
| `CEDictionary()` / `CEDictionary(int)` | Constructors. The capacity is a slot count: the table rebuilds at 75% full, so room for `n` entries without a rebuild needs a capacity of at least `n / 0.75`. |
| `Count` | Number of entries. |
| `this[TKey key]` | Indexer with lookup and add behavior. Reading a missing key is fatal in the same way as an out-of-range list index; use `TryGetValue` or `ContainsKey` when the key may be absent. |
| `Add`, `Remove`, `TryGetValue` | Core dictionary operations. |
| `ContainsKey`, `ContainsValue` | Search helpers. |
| `GetKeys`, `GetValues` | Snapshot arrays of keys or values, in matching but otherwise unspecified order. |
| `ToDataDictionary()` | Convert to `DataDictionary` (string keys recommended for JSON). |
| `ToDataDictionary(Func<TKey, DataToken>, Func<TValue, DataToken>)` | Convert with custom converters. Editor / plain C# only. |
| `FromDataDictionary(DataDictionary)` | Create from a `DataDictionary`. |
| `FromDataDictionary(DataDictionary, Func<DataToken, TKey>, Func<DataToken, TValue>)` | Create with converters. Editor / plain C# only. |
| `ToJson()` / `ToJson(bool)` | Serialize to JSON. |
| `FromJson(string)` | Deserialize from JSON; numbers are converted to `TValue`. |
| `FromJson(string, Func<DataToken, TKey>, Func<DataToken, TValue>)` | Deserialize with custom converters. Editor / plain C# only. |

Removing entries never grows the table. A removal leaves a tombstone, and once tombstones pass a quarter of the slots the table is rebuilt at the same size, so under constant add/remove churn the capacity stays within a small multiple of the most entries held at once, however many removals there have been.

### CEDataBridge

Editor / plain C# only: registration stores converter delegates.

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

In an UdonSharpBehaviour this does not get as far as `ToDataList`: `Vector3` is not `IComparable`, so `CEList<Vector3>` does not compile there at all.

### Good (UdonSharpBehaviour)

Keep the values in an array and build the `DataList` yourself.

```csharp
using UnityEngine;
using VRC.SDK3.Data;

Vector3[] points = _points;

DataList data = new DataList();
for (int i = 0; i < _pointCount; i++)
{
    Vector3 v = points[i];

    DataDictionary dict = new DataDictionary();
    dict["x"] = v.x;
    dict["y"] = v.y;
    dict["z"] = v.z;
    data.Add(dict);
}
```

### Good (editor / plain C#)

Outside Udon the constraint and the delegate restriction do not apply, and the converter overload does the same thing.

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
