# Contributing to UdonSharp CE

Contributions are welcome. **There is no CLA** — the upstream VRChat Contributor License Agreement
does not apply to this fork.

## Where to report an issue

| Problem | Where |
| --- | --- |
| A CE feature (`CE.*` modules, CE analyzers, CE editor tooling) | [this repo's issues](https://github.com/Furroxide/UdonSharpCE/issues) |
| UdonSharp itself, or the VRChat SDK | [vrchat-community/UdonSharp](https://github.com/vrchat-community/UdonSharp/issues) |

CE-specific code lives under `Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/` and
`Packages/com.merlin.UdonSharp/Editor/CE/`. If the file you're reporting against isn't in one of
those, it's likely an upstream issue.

Please include your **Unity version**, **VRChat SDK version**, and **CE version**
(`Packages/com.merlin.UdonSharp/package.json`).

## Running the tests

The suite that actually runs is **`CEShowcase.Tests`** (edit mode), in
`Assets/Scripts/CEShowcase/Tests/Editor/`.

1. Open the project in **Unity 2022.3.22f1** (see `ProjectSettings/ProjectVersion.txt`).
2. Install the VRChat Worlds SDK.
3. **Window → General → Test Runner → EditMode**.
4. Run the `CEShowcase.Tests` assembly. It is currently 107 tests and takes roughly two minutes.

Notes that will save you time:

- **`Packages/com.merlin.UdonSharp/Tests~/` does not run.** Unity excludes `~`-suffixed folders from
  the asset pipeline, so there is no `UdonSharp.CE.Tests` assembly. Those files are reference
  material; a test added there executes never. Put runnable tests in `CEShowcase.Tests`.
- **One C# compile error anywhere** fails a batch of unrelated source-generator tests with
  *"All Unity C# compiler errors must be resolved before running an UdonSharp compile."* Check the
  console for `error CS` first rather than debugging the cascade.
- **There is no ClientSim in this project**, so `Networking.LocalPlayer` is null and `Interact()`
  early-returns. Test the logic beneath it directly.
- Analyzer tests bind fixtures from source strings, which requires the fixture to be registered as a
  real assembly first — see `RegisterFixtureAssembly` in `CESyncAuthorityTests.cs` and the comment
  above its call site explaining why.

## Compiler and analyzer changes

CE ships compile-time diagnostics (`CE0001`–`CE0063`). If you add or change one:

- Add a regression test pinning both the positive and negative case.
- **Prefer a false negative to a false positive.** A diagnostic that fires on correct code trains
  authors to ignore the whole set — CE0063's Manual arm was removed for exactly this reason after it
  produced four distinct false-positive classes. That history is recorded in the analyzer's type
  comment; please read it before re-adding anything similar.
- Diagnostic text is user-facing. It must describe what the code actually does, not what it was
  intended to do.

## Pull requests

- Branch from `master`.
- Keep commits focused; explain *why* in the body, not just what.
- Run the test suite before opening the PR and say whether it passed.
- Documentation lives in `Tools/Docusaurus/docs/`; update it alongside behaviour changes.
