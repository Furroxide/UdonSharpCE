# Changelog

Notable changes to UdonSharp CE. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Changes to the underlying UdonSharp compiler inherited from
[upstream](https://github.com/MerlinVR/UdonSharp) are not repeated here.

## [Unreleased]

### Added
- `UdonSharp.CE.Net.Authority` — declares at the field who may write a synced value.
  `[Sync(Authority.Owner)]` keeps the strict ownership check; `[Sync(Authority.Predicted)]` states
  that every client writes it locally and the owner's value reconciles it. Authority is a required
  positional argument, because wrong interpolation merely looks janky while wrong authority is a
  silent desync.
- `CE0063` — reports an `Authority.Predicted` field on a behaviour whose declared sync mode
  replicates no variables, so the prediction can never converge.

### Changed
- `CE0014` is authority-aware: `Predicted` fields are exempt, `Owner` fields get the strict check,
  and bare `[UdonSynced]` keeps its existing inference path unchanged.
- **Breaking:** `[Sync]` now requires an `Authority` argument. `[Sync]` and
  `[Sync(InterpolationMode.X)]` no longer compile; interpolation moves to the second positional
  argument or the named `Interpolation` property.

### Fixed
- `CE0014` no longer reports a write guarded by an ownership helper declared on a base class — a
  pre-existing false positive, despite that shape being documented as supported.
- Tooling suggestions and XML docs no longer recommend `[Sync]` syntax that fails to compile.

### Removed
- Upstream CI that cannot run on this fork (VRChat CLA enforcement, Netlify previews, the private
  VRChat docs builder), and the DocFx/Nuke doc pipeline, which regenerated `Tools/Docusaurus/docs`
  from a source directory that does not exist here and would have deleted the hand-written docs.
