# These tests do not run. They never have.

`Tests~` is a `~`-suffixed folder, which Unity **excludes from the asset pipeline entirely**. Nothing in
here is compiled, no assembly is produced, and no test in here has ever been executed — including on the
occasions a session reported these suites as "passing".

`"testables": ["com.merlin.udonsharp"]` in the project manifest does not change this. That setting only
surfaces test assemblies that are already inside the package's asset pipeline, and a `~` directory is
invisible to it, so it currently does nothing at all for these files.

## Why the folder is still here

Un-tilde-ing it is **not** the fix. `Tests~/` also contains `UdonSharp.Tests.asmdef`, a real
`UdonSharpAssemblyDefinition` marker, and ~50 upstream U# integration behaviours, scenes and prefabs.
Importing it would feed every one of those into `CompilationContext.GetBuildAssemblies`, and therefore into
**every** UdonSharp compile in the project.

## Where the coverage actually lives now

The CE suites that matter were ported to `Assets/Scripts/CEShowcase/Tests/Editor/`, which is a real,
running EditMode assembly (`CEShowcase.Tests`):

| Was here | Now |
| --- | --- |
| `DevTools/SyncAttributeToolingTests.cs` | `CESyncAttributeToolingTests.cs`, rewritten against the validators' public surface instead of private methods reached by reflection |
| `Net/SyncOnJoinEmitterTests.cs` | superseded by `UdonSharpCESyncPipelineTests.cs` plus `UdonSyncVerificationTests.cs`, which check the **emitted Udon program** rather than .NET semantics |
| `Net/QuantizeEmitterTests.cs` | superseded by `UdonSharpCESyncPipelineTests.QuantizeEmitter_*` |
| `Net/SyncAttributeIntegrationTests.cs` | superseded by the `SyncAttribute_*` tests in `UdonSharpCESyncPipelineTests.cs` |
| `Serialization/UdonVariableStorageInterfaceThreadTests.cs` | superseded by `UdonVariableStorageInterface_ClearCache_IsThreadSafe` |
| `Net/SyncOnJoinCoordinatorTests.cs` | deleted — the coordinator it tested no longer exists |

Three assertions here still have no equivalent and are worth porting if anyone picks this up:
`QuantizeEmitter_ClassifiesAliasTypesFromSemanticModel`,
`SyncOnJoinEmitterTests.SyncOnJoinEmitter_ClassifiesAliasTypesFromSemanticModel` (its `_CE_SyncOnJoinInbound`
assertions describe the retired coordinator shape and would need rewriting against the current payload
design), and `SyncAttribute_NamedInterpolationProperty_ProducesLinearSyncMetadata`.

**This folder can be deleted once the porting above is confirmed and the repository has been committed** —
these files are currently untracked, so deleting them now would be unrecoverable.
