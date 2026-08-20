# Security Policy

## Scope

UdonSharp CE is a compiler and editor tooling for VRChat worlds. The most relevant classes of issue
are compiler or analyzer bugs that cause incorrect or unsafe code to be emitted, and networking
defects that let a client write state it should not own.

Vulnerabilities in **VRChat itself, Udon, or the VRChat SDK** are out of scope here — report those
to [VRChat](https://hackerone.com/vrchat). Bugs in upstream UdonSharp belong at
[vrchat-community/UdonSharp](https://github.com/vrchat-community/UdonSharp/issues).

## Reporting

Please report security issues privately using
[GitHub's private vulnerability reporting](https://github.com/Furroxide/UdonSharpCE/security/advisories/new)
rather than opening a public issue.

Include the affected version, what an attacker or a malicious client could achieve, and a minimal
reproduction if you have one.

## Supported versions

CE is developed on `master`. Fixes land there first; there is no long-term support branch.
