# UdonSharp

## A compiler for compiling C# to Udon assembly

UdonSharp is a compiler that compiles C# to Udon assembly. UdonSharp is not currently conformant to any version of the C# language specification, so there are many things that are not implemented or will not work.

## UdonSharp Community Edition

UdonSharp CE adds runtime libraries and editor tooling for async workflows, data bridges, networking analysis, persistence helpers, performance tooling, and procedural generation.

Start here: [Community Edition Overview](ce-overview)

## C# features supported

- Flow control
  - Supports: `if` `else` `while` `for` `do` `foreach` `switch` `return` `break` `continue` `ternary operator (condition ? true : false)` `??`
- Implicit and explicit type conversions
- Arrays and array indexers
- All builtin arithmetic operators
- Conditional short circuiting `(true || CheckIfTrue())` will not execute CheckIfTrue()
- `typeof()`
- Extern methods with out or ref parameters (such as many variants of `Physics.Raycast()`)
- Generic extern method calls (for example, `VRC.SDKBase.Utilities.ShuffleArray<T>()`)
- User defined methods with parameters and return values, supports out/ref, extension methods, and `params`
- User defined properties
- Static user methods
- UdonSharpBehaviour inheritence, virtual methods, etc.
- Unity/Udon event callbacks with arguments. For instance, registering a OnPlayerJoined event with a VRCPlayerApi argument is valid.
- String interpolation
- Expression-bodied constructors
- Field initializers
- User-defined enums, including comparison operators and default parameter values
- Object initializers (e.g., `new Foo { Bar = 1, Baz = 2 }`)
- Jagged arrays
- Referencing other custom UdonSharpBehaviour classes, accessing fields, and calling methods on them
- Recursive method calls are supported via the `[RecursiveMethod]` attribute
- `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>` and `Stack<T>`, compiled to Udon by
  UdonSharp's own implementations (see the notes on each type's limits in [CE Data](ce-data))

## C# features rejected at compile time

These compile as plain C# but have no faithful Udon translation, so UdonSharp stops with an error at the
line that uses them instead of producing a program that behaves differently:

- Constructor chaining (`: this(...)`, or `: base(...)` with arguments). Initialize the fields in the
  constructor itself.
- Explicit static constructors. Use static field initializers.
- `as` to a type Udon cannot test at runtime: user classes, UdonSharpBehaviour types, and types UdonSharp
  stores as `object[]` (its collections and jagged arrays). `as` to Unity, VRChat and system types works.
- `foreach` over CE collections (`CEList<T>`, `CEDictionary<TKey, TValue>`). Use a `for` loop over
  `Count` and the indexer, or `GetKeys()` for a dictionary.
- User-defined conversion operators.

## Differences from regular Unity C# to note

- For the best experience making UdonSharp scripts, make your scripts inherit from `UdonSharpBehaviour` instead of `MonoBehaviour`
- If you need to call `GetComponent<UdonBehaviour>()` you will need to use `(UdonBehaviour)GetComponent(typeof(UdonBehaviour))` at the moment since the generic get component is not exposed for UdonBehaviour yet. `GetComponent<T>()` works for other Unity component types though.
- Udon itself only has arrays. UdonSharp compiles `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>` and `Stack<T>` onto arrays for you, so they cost more than a plain array and are not interchangeable with the .NET types at runtime (for example, `.GetType()` does not return the .NET type).
- Field initilizers are evaluated at compile time, if you have any init logic that depends on other objects in the scene you should use Start for this.
- Use the `UdonSynced` attribute on fields that you want to sync.
- Numeric casts are checked for overflow due to UdonVM limitations
- The internal type of variables returned by `.GetType()` will not always match what you may expect since U# abstracts some types in order to make them work in Udon. For instance, any jagged array type will return a type of `object[]` instead of something like `int[][]` for a 2D int jagged array.

## Udon bugs that affect U#

- Mutating methods on structs do not modify the struct (this can be seen on things like calling Normalize() on a Vector3) https://vrchat.canny.io/vrchat-udon-closed-alpha-bugs/p/raysetorigin-and-raysetdirection-not-working

## Setup

### Requirements

- Unity 2022.3 (the version the VRChat Creator Companion installs)
- The VRChat Worlds SDK, 3.8.1 or newer

### Installation

UdonSharp CE replaces UdonSharp in a project. Do not install both.

1. In the [VRChat Creator Companion](https://vcc.docs.vrchat.com/), open Settings → Packages → Add
   Repository and add `https://vrc.furroxide.dev/vpm/index.json`.
2. Open your world project in the Creator Companion. If it already lists UdonSharp, remove it first.
3. Add **UdonSharp CE** from the package list.

See [Setup](setup) for more detail.

### Getting started

1. Make a new object in your scene
2. Add an `Udon Behaviour` component to your object
3. Below the "New Program" button click the dropdown and select "Udon C# Program Asset"
4. Now click the New Program button, this will create a new UdonSharp program asset for you
5. Click the Create Script button and choose a save destination and name for the script.
6. This will create a template script that's ready for you to start working on, open the script in your editor of choice and start programming

#### Asset explorer asset creation

Instead of creating assets from an UdonBehaviour you can also do the following:

1. Right-click in your project asset explorer
2. Navigate to Create > U# script
3. Click U# script, this will open a create file dialog
4. Choose a name for your script and click Save
5. This will create a .cs script file and an UdonSharp program asset that's set up for the script in the same directory

### Example scripts

#### The rotating cube demo

This rotates the object that it's attached to by 90 degrees every second

```cs
using UnityEngine;
using UdonSharp;

public class RotatingCubeBehaviour : UdonSharpBehaviour
{
    private void Update()
    {
        transform.Rotate(Vector3.up, 90f * Time.deltaTime);
    }
}
```

#### Other examples

For more example scripts take a look at the wiki page for [examples](https://github.com/Merlin-san/UdonSharp/wiki/examples), the Examples folder included with U#, or the [community resources](https://github.com/Merlin-san/UdonSharp/wiki/community-resources) page on the wiki.

## Credits

- See [CONTRIBUTORS.md](https://github.com/vrchat-community/UdonSharp/blob/master/CONTRIBUTORS.md) for people who have helped provide improvments to UdonSharp
- The open source project [Harmony](https://github.com/pardeike/Harmony) helps Udonsharp provide a better editor experience

#

[![Discord](https://img.shields.io/badge/Discord-Merlin%27s%20Discord%20Server-blueviolet?logo=discord)](https://discord.gg/Ub2n8ZA)
