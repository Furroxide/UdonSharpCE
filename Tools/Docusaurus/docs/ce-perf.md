# CE Perf

CE.Perf provides ECS-lite utilities and performance helpers for high-entity worlds.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Perf;
using UnityEngine;

public class BulletSystem : UdonSharpBehaviour
{
    private CEWorld _world;
    private Vector3[] _positions;
    private Vector3[] _velocities;
    private int _positionSlot;
    private int _velocitySlot;

    private const int PositionType = ComponentTypeId.CustomBase + 0;
    private const int VelocityType = ComponentTypeId.CustomBase + 1;

    void Start()
    {
        _world = new CEWorld(512);
        _positions = new Vector3[_world.MaxEntities];
        _velocities = new Vector3[_world.MaxEntities];

        _positionSlot = _world.RegisterComponent(PositionType, _positions);
        _velocitySlot = _world.RegisterComponent(VelocityType, _velocities);

        _world.RegisterSystem(this, nameof(UpdateMovement));
    }

    void Update()
    {
        _world.Tick();
    }

    public void UpdateMovement()
    {
        int[] entities = _world.ActiveEntities;
        int count = _world.ActiveEntityCount;

        for (int i = 0; i < count; i++)
        {
            int id = entities[i];
            if (!_world.HasComponent(id, _positionSlot) || !_world.HasComponent(id, _velocitySlot))
                continue;

            _positions[id] += _velocities[id] * Time.deltaTime;
        }
    }
}
```

## What Runs In Udon

Udon has no delegates. CE inlines a lambda at compile time only for `List<T>.ForEach` and the `CEList<T>` ForEach helpers (see CE Core), so any other member that takes or stores an `Action` or `Func` can be called from editor and plain C# code but not from an UdonSharpBehaviour. In this module that splits as follows:

| No delegates involved | Takes or stores a delegate: editor / plain C# only |
| --- | --- |
| `CEWorld` | |
| `CEQuery`: `With`, `Without`, `Execute`, `Count`, `First`, `Any`, `RefreshCache`, `GetCached` | `CEQuery.ForEach`, `ForEachWhile`, `ForEachCached` |
| `CEGrid`: everything except the member on the right | `CEGrid.ForEachNonEmptyCell` |
| `CELod` | |
| | `CEPool<T>` (its factory and callbacks are delegates) |
| | `BatchProcessor` (every `Process*` method takes a delegate) |

For pooling in a behaviour, keep your own free stack of indices, as the showcase `BulletPool` does. For time-slicing, keep the index in a field and advance it by a fixed step each `Update`.

## API Reference

### CEWorld

| Member | Description |
| --- | --- |
| `CEWorld(int maxEntities)` | Create a world with fixed capacity. |
| `CreateEntity()` / `DestroyEntity()` | Allocate and free entities. Destroying clears the entity's component mask, not the data in your arrays. |
| `DestroyEntityDeferred()` | Destroy at the end of the tick. Removal from `ActiveEntities` is immediate; only id recycling is deferred. |
| `AddComponent()` / `RemoveComponent()` | Assign component slots to entities. |
| `RegisterComponent(int typeId, object array)` | Register a component array and get its slot (0-31). |
| `RegisterSystem(UdonSharpBehaviour, string)` | Register a system callback by name. Returns an index that stays valid for `EnableSystem` / `DisableSystem`. |
| `RegisterSystem(CECallback)` | Register a system via `CECallback`. |
| `Tick()` | Execute systems in `order`, then process deferred destructions. |
| `ActiveEntities` / `ActiveEntityCount` | Dense, unordered list of active entity IDs. |
| `PendingDestroyCount` | Entities currently waiting for the deferred flush. |

A system is a **public, parameterless** method: `Tick()` invokes it by name, and a private method or one with parameters is silently never called.

### CEQuery

| Member | Description |
| --- | --- |
| `With(int slot)` / `Without(int slot)` | Build component filters. |
| `Execute(int[] result)` | Copy matching entity IDs into your buffer. |
| `Count()` / `First()` / `Any()` | Count matches, or get the first one. |
| `ForEach(Action<int>)` | Iterate matching entities. Editor / plain C# only. |

### CEPool&lt;T&gt;

Editor / plain C# only; see [What Runs In Udon](#what-runs-in-udon).

| Member | Description |
| --- | --- |
| `Initialize(Func<int, T>, Action<T>, Action<T>)` | Create pooled objects and callbacks. |
| `Acquire()` / `Release(T)` | Borrow and return objects. |
| `AcquireHandle()` / `Release(PoolHandle<T>)` | O(1) handle-based operations. A handle releases only the acquisition it came from; releasing it twice returns `false` the second time. |

### CEGrid

| Member | Description |
| --- | --- |
| `Insert(int id, Vector3 pos)` | Add an entity. Returns `false` if the position is outside the bounds or the cell is full. |
| `Remove(int id)` | Remove an entity by searching every cell. |
| `RemoveFromCell(int id, int cellIndex)` | Remove an entity from a known cell. |
| `UpdatePosition(int id, Vector3 oldPos, Vector3 newPos)` | Move an entity between cells. |
| `PositionToCellIndex(Vector3)` | Cell index for a position, or `-1` outside the bounds. |
| `QueryRadius(Vector3, float, int[] result)` / `QueryBox(Vector3, Vector3, int[] result)` | Query nearby entities. |
| `Clear()` | Clear all cells. |

Both faces of the bounds are inside the grid, so a position clamped to `boundsMin` or `boundsMax` is still stored and still found. Queries return every entity in the cells the query volume touches; filter by distance afterwards if you need an exact radius.

### CELod

| Member | Description |
| --- | --- |
| `SetLevel(int, float, float, int)` | Configure distance bands and update intervals. |
| `ShouldUpdate(Vector3 a, Vector3 b)` | Check whether to update a target this frame. |
| `Update(int frame)` | Update the current frame counter. |

### BatchProcessor

Editor / plain C# only; see [What Runs In Udon](#what-runs-in-udon).

| Member | Description |
| --- | --- |
| `Process(int total, Action<int>)` | Process a batch each frame. |
| `ProcessWhile(int total, Func<int, bool>)` | Stop early by returning false. |
| `ProcessContinuous(int total, Action<int>)` | Loop continuously over items. |
| `Reset()` / `ResetAll()` | Reset state. |

`total` may change between calls. The pass keeps its position and carries on against the new count; it does not start over.

## Destroying Entities While Iterating

`ActiveEntities` is edited in place. `DestroyEntity`, `DestroyEntityDeferred` and `DisableEntity` all remove the entity **immediately** by swap-remove: the last entry moves into the vacated index and `ActiveEntityCount` drops by one. `CreateEntity`, `EnableEntity` and `CancelDestruction` append at the end.

`DestroyEntityDeferred` therefore does not make a loop over `ActiveEntities` safe. What it defers is the recycling: until the flush at the end of `Tick()` (or `FlushPendingDestructions()`), the id is not handed out again and the entity's component mask is kept, so code still holding the id reads this entity's data and not a new one's.

A forward loop over `ActiveEntities` is only correct while the loop body leaves the entity set alone. If it removes an entity it has not reached yet, the moved entry is visited twice; if it removes the current entity and then creates one, the moved entry is skipped.

Loops that hold up when entities are removed:

```csharp
// 1. Backwards, removing only the entity being visited (or ones already visited).
int[] active = _world.ActiveEntities;
for (int i = _world.ActiveEntityCount - 1; i >= 0; i--)
{
    int id = active[i];
    if (_lifetimes[id] <= 0f)
        _world.DestroyEntityDeferred(id);
}

// 2. Over a copy, checking each id. Use this when a system may remove entities other
//    than the current one. Defer destruction and do not flush the destroy queue
//    or reuse ids until this pass ends.
int count = _world.GetActiveEntities(_scratchIds);
for (int i = 0; i < count; i++)
{
    int id = _scratchIds[i];
    if (!_world.IsValidEntity(id))
        continue; // removed earlier in this loop

    // ... use DestroyEntityDeferred; do not recycle ids during this pass
}
```

The copy relies on `IsValidEntity`, which only says whether an id is active. An id destroyed with `DestroyEntity` goes straight back on the free list, so a `CreateEntity` later in the same loop can hand it to a new entity, which then passes the check in the old entity's place. A deferred id is not handed out again until the flush, which is why the second form defers.

Scanning ids `0..MaxEntities` and testing `GetEntityState(id)` does not depend on the list at all; it is how the Bullet Storm showcase works through its destroy queue.

A recycled id keeps whatever the previous entity left in your component arrays. Write a component's initial value when you `AddComponent` it.

## Notes On Component Attributes

`CEComponent` and `CESystem` are metadata only. CEWorld does not auto-register components or systems based on these attributes yet.

## Notes On Component Slots

A world tracks components in one 32-bit mask per entity, so it holds at most 32 registered components; the 33rd `RegisterComponent` returns `-1`. `ComponentTypeId.MaxTypes` (256) bounds the type id space, not this. The type id is only a label: `AddComponent`, `HasComponent` and `CEQuery` take the **slot** that `RegisterComponent` returned.

## Common Pitfalls

### Bad

```csharp
_world.RegisterComponent(ComponentTypeId.Vector3, positions);
_world.RegisterComponent(ComponentTypeId.Vector3, velocities); // Same typeId used twice
```

### Good

```csharp
const int PositionType = ComponentTypeId.CustomBase + 0;
const int VelocityType = ComponentTypeId.CustomBase + 1;
_world.RegisterComponent(PositionType, positions);
_world.RegisterComponent(VelocityType, velocities);
```
