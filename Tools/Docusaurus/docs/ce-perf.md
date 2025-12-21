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

## API Reference

### CEWorld

| Member | Description |
| --- | --- |
| `CEWorld(int maxEntities)` | Create a world with fixed capacity. |
| `CreateEntity()` / `DestroyEntity()` | Allocate and free entities. |
| `AddComponent()` / `RemoveComponent()` | Assign component slots to entities. |
| `RegisterComponent(int typeId, object array)` | Register a component array and get its slot. |
| `RegisterSystem(UdonSharpBehaviour, string)` | Register a system callback by name. |
| `RegisterSystem(CECallback)` | Register a system via `CECallback`. |
| `Tick()` | Execute systems and process deferred destructions. |
| `ActiveEntities` / `ActiveEntityCount` | Dense list of active entity IDs. |

### CEQuery

| Member | Description |
| --- | --- |
| `With(int slot)` / `Without(int slot)` | Build component filters. |
| `Execute(int[] result)` | Get matching entity IDs. |
| `ForEach(Action<int>)` | Iterate matching entities. |

### CEPool<T>

| Member | Description |
| --- | --- |
| `Initialize(Func<int, T>, Action<T>, Action<T>)` | Create pooled objects and callbacks. |
| `Acquire()` / `Release(T)` | Borrow and return objects. |
| `AcquireHandle()` / `Release(PoolHandle<T>)` | O(1) handle-based operations. |

### CEGrid

| Member | Description |
| --- | --- |
| `Insert(int id, Vector3 pos)` / `Remove(int id, Vector3 pos)` | Update the grid. |
| `QueryRadius(Vector3, float, int[] result)` | Query nearby entities. |
| `Clear()` | Clear all cells. |

### CELod

| Member | Description |
| --- | --- |
| `SetLevel(int, float, float, int)` | Configure distance bands and update intervals. |
| `ShouldUpdate(Vector3 a, Vector3 b)` | Check whether to update a target this frame. |
| `Update(int frame)` | Update the current frame counter. |

### BatchProcessor

| Member | Description |
| --- | --- |
| `Process(int total, Action<int>)` | Process a batch each frame. |
| `ProcessWhile(int total, Func<int, bool>)` | Stop early by returning false. |
| `ProcessContinuous(int total, Action<int>)` | Loop continuously over items. |
| `Reset()` / `ResetAll()` | Reset state. |

## Notes On Component Attributes

`CEComponent` and `CESystem` are metadata only. CEWorld does not auto-register components or systems based on these attributes yet.

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
