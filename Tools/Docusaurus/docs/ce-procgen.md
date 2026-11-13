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
| `CERandom(int seed)` | Deterministic xoroshiro128+ generator. Every int seed works, negative ones included, and gives the same sequence on every client. |
| `CERandom()` | Seeded from the clock, so clients do not agree. Use it only for purely local effects. |
| `Range(int min, int maxExclusive)` | An int in `[min, maxExclusive)`; returns `min` without consuming a draw when the range is empty. Works for any span, including `Range(int.MinValue, int.MaxValue)`. |
| `RangeInclusive(int min, int max)` | An int in `[min, max]`, including `max = int.MaxValue`. |
| `Range(float, float)` / `Range(double, double)` | A value in `[min, max)`. |
| `NextInt()` / `NextFloat()` / `NextDouble()` | A non-negative int, a float in `[0, 1)`, a double in `[0, 1)`. |
| `Chance(float)` | Random probability test. |
| `InsideUnitSphere()` / `OnUnitSphere()` | Random directions. |
| `Shuffle<T>(T[])` | Deterministic Fisher-Yates shuffle. |
| `WeightedChoice(float[])` | Weighted selection; returns -1 when no weight is positive. |

### CENoise

| Member | Description |
| --- | --- |
| `Initialize(int seed)` | Set this behaviour's noise seed; initialize in every consuming behaviour. |
| `Perlin2D`, `Perlin3D` | Classic Perlin noise. |
| `Simplex2D`, `Simplex3D` | Simplex noise. |
| `Worley2D`, `Worley3D` | Worley noise. |
| `Fractal2D/3D`, `Ridged2D/3D`, `Turbulence2D` | Fractal noise variants. Fewer than one octave returns 0. |

### CEDungeon

| Member | Description |
| --- | --- |
| `CEDungeon.Generate(CERandom, DungeonConfig)` | Generate a dungeon layout in one call. |
| `DungeonConfig` | Room sizes, counts, connectivity, bounds. Every room lies inside `(0, 0)` to `MaxBounds`; a configured room size larger than the bounds is shrunk to fit. |
| `DungeonLayout` | `Rooms`, `Corridors`, `CriticalPath`, and the overall area as `BoundsMin` plus `BoundsSize`. |
| `DungeonRoom` | `Position`, `Size`, `Max` (top-right corner, exclusive), `Type`, `ConnectedRooms`, `IsOnCriticalPath`. |
| `DungeonCorridor` | `FromRoom`, `ToRoom`, `Path`, `Width`, and `IsOnCriticalPath` for the corridors between consecutive critical-path rooms. |

### WFCSolver

| Member | Description |
| --- | --- |
| `WFCSolver(WFCConfig, int seed)` | Create a solver. An unusable configuration (no tiles, more than 64, a null tile, a negative size) starts as `Failed`. |
| `Tick()` | Run up to `MaxIterationsPerFrame` iterations (at least one). |
| `Solve()` | Run to completion (can be heavy). |
| `SetTile(x, y, id)` | Force a tile before solving. Refused, leaving the grid untouched, when the constraints already exclude it; `Failed` when it empties a neighbour. |
| `GetResult()` | Flat tile IDs, `Width * Height` long: cell `(x, y)` is at `x * Height + y`. |
| `GetTileAt(x, y)` / `IsCollapsed(x, y)` | One cell. Use `IsCollapsed` to tell an uncollapsed cell from a tile whose ID is -1. |

WFC accepts 1–64 tiles. Tile IDs may be any int, negative ones included. A tile whose weight is zero or less is never chosen while a weighted tile is still possible, but is placed where the constraints leave nothing else. A contradiction returns `Failed`; there is no backtracking. Reset repeats the same seed, so use a different seed or rules when retrying.

Procgen state lives in one-dimensional arrays and `Vector2Int` values: UdonSharp cannot compile multidimensional arrays, and Udon does not expose `RectInt`.

## Common Pitfalls

### Bad

```csharp
var rng = new CERandom(); // Time-based seed; clients need not agree.
```

### Good

```csharp
var rng = new CERandom(seed); // Same seed yields identical results
```
