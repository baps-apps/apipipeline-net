# Versioning and Compatibility Policy

The three packages follow [Semantic Versioning](https://semver.org/) and ship together under one
version (`src/Shared/Shared.props`). Use the same version of each.

- **MAJOR**: you may need to change code or configuration.
- **MINOR**: new features and options with defaults that keep current behaviour.
- **PATCH**: fixes only.

Within a major version, valid configuration stays valid, except that validation may get stricter to
block unsafe or ambiguous settings (the error says what to change). Package validation compares
each package's public API with the previous release on every build.

## Supported versions

| Version | Target framework | Status |
| --- | --- | --- |
| 2.x | `net10.0` | Current; adds `ApiPipeline.NET.RateLimiting.Redis` |
| 1.x | `net10.0` | Superseded; fixes only in 2.x |

The Redis package supports Redis 5.0+. Raising that floor, or changing the key format or script
semantics (pods mid-rollout would count separately), is a major change.

## Deprecation

An API or option is first marked `[Obsolete]` naming its replacement, documented in the
[migration guide](migration.md), and removed only in the next major version. 2.0.0 removed some 1.x
APIs without this step; the migration guide lists each.

## Upgrading

| Upgrade | What to do |
| --- | --- |
| Patch | Run your tests |
| Minor | Read the [CHANGELOG](../CHANGELOG.md), run your tests |
| Major | Follow the [migration guide](migration.md); roll out in stages |
