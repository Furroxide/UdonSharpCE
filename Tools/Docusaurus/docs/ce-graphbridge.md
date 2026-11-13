# CE GraphBridge

CE.GraphBridge marks C# methods and properties with attributes that its editor tools read: a node browser, a validator, a wrapper generator and a documentation generator.

GraphBridge does not add nodes to VRChat's Udon Graph editor. Nothing registers these definitions with the Udon Graph; the tools below all work from the same in-editor list of attributed methods and properties.

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

Browse the result with `Udon CE/Graph Bridge/Node Browser`, or write wrapper classes with `Udon CE/Graph Bridge/Generate Wrappers`.

## API Reference

### Node Attributes

| Attribute | Description |
| --- | --- |
| `GraphNode` | Expose a public method as a node: menu path, `IsFlowNode`, `Icon`, `Color` and `Tooltip`. |
| `GraphInput` | Display name, default value, tooltip and hidden flag of an input port. |
| `GraphOutput` | Display name and tooltip of the port for an `out` parameter. |
| `GraphFlowOutput` | Name a flow output of a branching node. |
| `GraphProperty` | Expose a property as Get and Set nodes; `ReadOnly` keeps only Get. |
| `GraphCategory` | Prefix the menu path of every node in a class. |

### Declared But Not Read

These compile, but no GraphBridge tool reads them:

- `GraphEvent` and `GraphTypeConstraint`, in full.
- `GraphNode.Category`, `GraphNode.Searchable` and `GraphNode.SearchKeywords`.
- `GraphCategory.Icon` and `GraphCategory.Priority`.
- `GraphInput.Min` and `GraphInput.Max`.
- `GraphFlowOutput.Tooltip`.

## Editor Tools

| Menu Path | Purpose |
| --- | --- |
| `Udon CE/Graph Bridge/Node Browser` | Browse and search nodes, see their ports and validation, and generate a wrapper for one node. |
| `Udon CE/Graph Bridge/Validate Nodes` | Log parameter and return types Udon cannot use. |
| `Udon CE/Graph Bridge/Generate Nodes` | Write a list of the nodes to `Assets/UdonSharpCE/GeneratedNodes/NodeManifest.json`. |
| `Udon CE/Graph Bridge/Generate Wrappers` | Write static wrapper classes to `Assets/UdonSharpCE/GeneratedWrappers/`. |
| `Udon CE/Graph Bridge/Generate Documentation` | Write Markdown pages into a folder you choose. |

### Generated Wrappers

- Each menu category becomes one file holding a class named `<Category>GraphWrappers` in the namespace `UdonSharp.CE.Generated`. A wrapper made for a single node from the Node Browser goes in a class named after the node's type and method.
- A wrapper calls the original static method by its fully qualified name. A wrapper for an instance method throws `NotImplementedException`, since it has no instance to call.
- A node that cannot be wrapped is skipped with a console warning: a generic method with constraints, a method of an open generic type, a member that is not public, an operator, a `ref` return, or a signature that uses a type from an editor-only, test or not auto-referenced assembly. The wrappers compile into `Assembly-CSharp`, which cannot see those.
- A file is rewritten only when its text changes. Files the generator wrote earlier that are no longer wanted are deleted; files in the folder that it did not write are never touched.

### Generated Documentation

The generator asks for a folder and remembers the choice for the project. It writes `overview.md`, a `_category_.json` and one page per node in a folder per category, in a form a Docusaurus site can build: text from names and tooltips is escaped for MDX, front matter and JSON.

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
