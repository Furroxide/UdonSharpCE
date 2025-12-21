# CE GraphBridge

CE.GraphBridge exposes C# methods and properties to graph tooling via attributes and editor generators.

## Quick Start

```csharp
using UdonSharp.CE.GraphBridge;
using UnityEngine;

public static class MathNodes
{
    [GraphNode("Math/Lerp Vector3")]
    public static Vector3 LerpVector3(
        [GraphInput("Start")] Vector3 a,
        [GraphInput("End")] Vector3 b,
        [GraphInput("T")] float t)
    {
        return Vector3.Lerp(a, b, t);
    }
}
```

Run the generator from the Unity menu: `Udon CE/Graph Bridge/Generate Nodes`.

## API Reference

### Node Attributes

| Attribute | Description |
| --- | --- |
| `GraphNode` | Expose a method as a node. |
| `GraphInput` | Customize input ports and defaults. |
| `GraphOutput` | Customize output ports for `out` parameters. |
| `GraphFlowOutput` | Define flow outputs for branching nodes. |
| `GraphProperty` | Expose properties as Get/Set nodes. |
| `GraphEvent` | Expose methods as graph events. |
| `GraphCategory` | Group node classes under a menu category. |
| `GraphTypeConstraint` | Restrict generic parameter types. |

## Editor Tools

| Menu Path | Purpose |
| --- | --- |
| `Udon CE/Graph Bridge/Node Browser` | Explore generated nodes. |
| `Udon CE/Graph Bridge/Validate Nodes` | Validate node definitions. |
| `Udon CE/Graph Bridge/Generate Nodes` | Generate node assets. |
| `Udon CE/Graph Bridge/Generate Wrappers` | Generate wrappers for graph access. |
| `Udon CE/Graph Bridge/Generate Documentation` | Generate graph node docs. |

## Common Pitfalls

### Bad

```csharp
[GraphNode("Math/Hidden")]
private static float Hidden(float v) { return v; } // Private methods are not exported
```

### Good

```csharp
[GraphNode("Math/Visible")]
public static float Visible(float v) { return v; }
```
