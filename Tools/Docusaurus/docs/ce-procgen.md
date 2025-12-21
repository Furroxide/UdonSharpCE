# CE Procgen

CE.Procgen provides deterministic random, noise, and layout generation tools for synchronized procedural content.

## Quick Start

```csharp
using UdonSharp;
using UdonSharp.CE.Procgen;
using UnityEngine;

public class ProcgenExample : UdonSharpBehaviour
{
    public int seed = 1234;

    void Start()
    {
        CERandom rng = new CERandom(seed);
        CENoise.Initialize(seed);

        float h = CENoise.Fractal2D(10f, 20f, octaves: 4);
        Vector3 randomDir = rng.OnUnitSphere();
        Debug.Log("Noise: " + h + " dir: " + randomDir);
    }
}
```

## API Reference

### CERandom

| Member | Description |
| --- | --- |
| `CERandom(int seed)` | Deterministic RNG with seed. |
| `Range(int, int)` / `Range(float, float)` | Random range helpers. |
| `NextFloat()` / `NextDouble()` | Random floats. |
| `Chance(float)` | Random probability test. |
| `InsideUnitSphere()` / `OnUnitSphere()` | Random directions. |
| `Shuffle<T>(T[])` | Deterministic shuffle. |
| `WeightedChoice(float[])` | Weighted selection by probability. |

### CENoise

| Member | Description |
| --- | --- |
| `Initialize(int seed)` | Set the global noise seed. |
| `Perlin2D`, `Perlin3D` | Classic Perlin noise. |
| `Simplex2D`, `Simplex3D` | Simplex noise. |
| `Worley2D`, `Worley3D` | Worley noise. |
| `Fractal2D/3D`, `Ridged2D/3D`, `Turbulence2D/3D` | Fractal noise variants. |

### CEDungeon

| Member | Description |
| --- | --- |
| `CEDungeon.Generate(CERandom, DungeonConfig)` | Generate a dungeon layout. |
| `DungeonConfig` | Room sizes, counts, connectivity, bounds. |
| `DungeonLayout` | Rooms, corridors, bounds, critical path. |

### WFCSolver

| Member | Description |
| --- | --- |
| `WFCSolver(WFCConfig, int seed)` | Create a solver. |
| `Tick()` | Run a limited number of iterations per frame. |
| `Solve()` | Run to completion (can be heavy). |
| `GetResult()` | Get the tile ID grid. |

## Common Pitfalls

### Bad

```csharp
var rng = new CERandom(); // Non-deterministic seed
```

### Good

```csharp
var rng = new CERandom(seed); // Same seed yields identical results
```
