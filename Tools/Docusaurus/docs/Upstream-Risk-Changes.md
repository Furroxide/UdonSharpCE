# Upstream Higher-Risk Changes

This document tracks upstream PRs that were evaluated as higher-risk for UdonSharpCE and not merged yet. It summarizes scope, risk, and the validation needed before adoption.

## vrchat-community/UdonSharp#102 - Improve switch flow

- Summary: Reworks jump-table switch generation to use a label table and `Array.IndexOf`, changing how case dispatch is performed for numeric switch values.
- Risk: Compiler/codegen behavior changes may mis-handle sparse or negative labels, duplicate labels, or enum-backed switches; performance characteristics change due to `Array.IndexOf`.
- Validation: Compile and run tests for switch statements with sparse values, negative values, enums, and default-only switches; compare generated Udon assembly with baseline outputs.

## vrchat-community/UdonSharp#105 - Merge nested prefab process and prefab variant process

- Summary: Treats prefab variants as nested prefabs at the root and removes the separate variant handling path in the prefab DAG.
- Risk: Changes the upgrade order and parent-child relationships for variant prefabs; may skip required upgrade edges or create incorrect ordering in complex prefab graphs.
- Validation: Run prefab upgrade passes on projects with variant prefabs, nested prefabs, and combined variant+nesting; verify upgrade ordering and serialized results.

## vrchat-community/UdonSharp#107 - Fix UdonSharpPrefabDAG constructor breaks prefabs

- Summary: Refactors DAG construction and sorting, adds stricter validation, and moves error handling to the upgrade path instead of falling back to unsorted prefabs.
- Risk: Stricter validation can halt upgrades that previously fell back; DAG changes can reorder upgrades and expose latent project issues.
- Validation: Run upgrades on real projects with nested/variant prefabs; verify that errors are actionable and that ordering is stable.

## MerlinVR/UdonSharp#129 - Add support for searching for alternate invocees for externs

- Summary: When an extern signature is not exposed, attempts to resolve an alternate invocation via base types or interfaces; updates compiler invocation handling and adds tests.
- Risk: Broad change in extern resolution can introduce incorrect method selection or unexpected exposure matches; PR targets the legacy `Assets/` layout and includes test scene changes that must be ported to `Packages/`.
- Validation: Run compiler tests covering extern calls on interfaces, base classes, and concrete types; verify that no unintended overloads are selected and that Udon exposure checks remain correct.
