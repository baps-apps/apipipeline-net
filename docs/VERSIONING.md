# Versioning and Compatibility Policy

This document defines versioning expectations for `ApiPipeline.NET`.

## Semantic versioning

`ApiPipeline.NET` follows [Semantic Versioning](https://semver.org/):

- **MAJOR** (`X.y.z`): breaking changes to public APIs, option contracts, middleware behavior, or defaults that require user action.
- **MINOR** (`x.Y.z`): backward-compatible features and additive configuration.
- **PATCH** (`x.y.Z`): bug fixes and internal improvements only.

## Compatibility guarantees

- Within a major line, existing valid configuration remains valid unless it was previously unsafe or ambiguous.
- New options are additive and have safe defaults.
- Startup validation may tighten to block clearly incorrect or insecure configurations.
- Option class property additions are non-breaking (new properties use defaults).

## Runtime support matrix

| ApiPipeline.NET | Target Framework | ASP.NET Core | Status |
|---|---|---|---|
| 1.x | `net10.0` | 10.x | Current |

Future major versions may adjust target frameworks in alignment with the [.NET support lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Deprecation policy

When APIs or options are scheduled for removal:

1. Mark as `[Obsolete]` with a descriptive message and document as deprecated.
2. Provide migration guidance in [migration.md](migration.md).
3. Remove only in a later **major** release.

Example:

```csharp
[Obsolete("Use PreferOutputCaching instead. Will be removed in 2.0.")]
public bool UseLegacyCaching { get; set; }
```

## Upgrade guidance

| Upgrade type | Risk | Action |
|---|---|---|
| **Patch** (`1.0.0` → `1.0.1`) | Low | Run existing tests. |
| **Minor** (`1.0.x` → `1.1.0`) | Low–medium | Review release notes for new optional features. Run tests. |
| **Major** (`1.x` → `2.0.0`) | High | Follow [migration.md](migration.md). Staged rollout recommended. |

## Related

- [CHANGELOG.md](../CHANGELOG.md) — version history and release notes.
- [migration.md](migration.md) — step-by-step upgrade instructions.
