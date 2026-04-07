# API Version Deprecation Headers

## What it does

ApiPipeline.NET's `ApiVersionDeprecationMiddleware` adds standard HTTP deprecation headers (`Deprecation`, `Sunset`, `Link`) to responses for deprecated API versions. This communicates deprecation status programmatically to API consumers, enabling automated tooling and client-side warnings.

## Why it matters

- **Standards-based**: uses the [HTTP Deprecation header](https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-deprecation-header) and [Sunset header (RFC 8594)](https://www.rfc-editor.org/rfc/rfc8594) specifications.
- **Client automation**: API consumers can detect deprecated versions programmatically and show warnings or trigger migration workflows.
- **Gradual migration**: deprecate versions without removing them — consumers get advance notice with clear sunset dates.
- **Zero runtime cost for non-deprecated routes**: the middleware short-circuits early when the request path or version doesn't match.

## How it works

```text
Request → Path starts with PathPrefix (default: /api)?
  ├── No → Pass through (no overhead)
  └── Yes → IApiVersionReader extracts version from request
              ├── Version not in DeprecatedVersions → Pass through
              └── Version found → Add headers via OnStarting callback:
                    • Deprecation: <date> or "true"
                    • Sunset: <date> (if configured)
                    • Link: <url>; rel="sunset" (if configured)
```

### Response headers example

```http
HTTP/1.1 200 OK
Deprecation: Tue, 01 Jul 2025 00:00:00 GMT
Sunset: Tue, 01 Jul 2026 00:00:00 GMT
Link: <https://docs.example.com/api-v1-migration>; rel="sunset"
Content-Type: application/json
```

### Prerequisites

The middleware requires an `IApiVersionReader` implementation to extract the API version from the request. Options:

1. **`ApiPipeline.NET.Versioning` satellite package** — provides an implementation backed by `Asp.Versioning`.
2. **Custom implementation** — implement the `IApiVersionReader` interface.

Without an `IApiVersionReader`, the middleware passes through silently and logs a debug message.

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// With Asp.Versioning (required for version reading)
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    // ...
    .WithVersionDeprecation()
);
```

## All available options

### `ApiVersionDeprecationOptions` (JSON section: `ApiVersionDeprecationOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch. When `false`, no deprecation headers are emitted. |
| `PathPrefix` | `string` | `"/api"` | URL path prefix that triggers deprecation checks. Only routes starting with this prefix are inspected. |
| `DeprecatedVersions` | `DeprecatedVersion[]` | `[]` | Array of deprecated version entries with metadata. |

### `DeprecatedVersion`

| Property | Type | Default | Description |
|---|---|---|---|
| `Version` | `string` | `""` | The API version string (e.g., `"1.0"`, `"2"`). Matched case-insensitively against the request. |
| `DeprecationDate` | `DateTimeOffset?` | `null` | Date the version was deprecated. Emitted as `Deprecation` header in RFC 1123 format. If `null`, `Deprecation: true` is emitted. |
| `SunsetDate` | `DateTimeOffset?` | `null` | Date the version will be removed. Emitted as `Sunset` header. |
| `SunsetLink` | `string?` | `null` | Absolute URL with migration/deprecation documentation. Must be a valid absolute URI. Emitted as `Link: <url>; rel="sunset"`. |

## Configuration examples

### Single deprecated version

```json
{
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "PathPrefix": "/api",
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "2025-07-01T00:00:00Z",
        "SunsetDate": "2026-07-01T00:00:00Z",
        "SunsetLink": "https://docs.example.com/api-v1-migration"
      }
    ]
  }
}
```

### Multiple deprecated versions

```json
{
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "PathPrefix": "/api",
    "DeprecatedVersions": [
      {
        "Version": "1.0",
        "DeprecationDate": "2024-01-01T00:00:00Z",
        "SunsetDate": "2025-01-01T00:00:00Z",
        "SunsetLink": "https://docs.example.com/v1-sunset"
      },
      {
        "Version": "2.0",
        "DeprecationDate": "2025-07-01T00:00:00Z",
        "SunsetDate": "2026-07-01T00:00:00Z",
        "SunsetLink": "https://docs.example.com/v2-sunset"
      }
    ]
  }
}
```

### Deprecated without sunset date (indefinite)

```json
{
  "DeprecatedVersions": [
    {
      "Version": "1.0",
      "DeprecationDate": "2025-01-01T00:00:00Z",
      "SunsetLink": "https://docs.example.com/v1-info"
    }
  ]
}
```

Produces: `Deprecation: Wed, 01 Jan 2025 00:00:00 GMT` (no `Sunset` header).

### Custom path prefix

```json
{
  "ApiVersionDeprecationOptions": {
    "Enabled": true,
    "PathPrefix": "/services/v",
    "DeprecatedVersions": [
      {
        "Version": "1",
        "DeprecationDate": "2025-01-01T00:00:00Z"
      }
    ]
  }
}
```

Only routes starting with `/services/v` are inspected.

## Production recommendations

| Recommendation | Why |
|---|---|
| Set deprecation dates well in advance | Give consumers time to migrate |
| Always include `SunsetLink` | Points consumers to migration documentation |
| Monitor `apipipeline.deprecation.headers_added` metric | Track how much traffic still hits deprecated versions |
| Communicate sunset timelines through multiple channels | Headers are machine-readable; also send emails/announcements |
| Don't remove versions silently | Remove only after the sunset date, with monitoring of remaining traffic |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Enable deprecation headers in staging | Verify headers are emitted correctly before production |
| Test client handling of deprecation headers | Ensure frontend/client code processes `Deprecation` and `Sunset` |
| Use past dates for deprecated versions in test fixtures | Verifies the middleware works regardless of date comparison |

## Startup validation

| Error | Cause | Fix |
|---|---|---|
| `SunsetLink must be a valid absolute URI` | `SunsetLink` is not a valid URL | Use a full URL: `https://docs.example.com/migration` |

Invalid `SunsetLink` values that pass startup validation but fail URI parsing at runtime are silently skipped with a warning log.

## Troubleshooting

### Deprecation headers not emitted

1. Verify `ApiVersionDeprecationOptions:Enabled` is `true`.
2. Verify `WithVersionDeprecation()` is called in `UseApiPipeline`.
3. Verify an `IApiVersionReader` is registered (install `ApiPipeline.NET.Versioning`).
4. Check the request path starts with `PathPrefix` (default: `/api`).
5. Check the requested version matches a `Version` in `DeprecatedVersions` (case-insensitive).

```bash
curl -s -D - https://api.example.com/api/v1/resource | grep -iE "deprecation|sunset|link"
```

### Debug log: "No IApiVersionReader registered"

Install the `ApiPipeline.NET.Versioning` satellite package or register a custom `IApiVersionReader`.

### `Sunset` header missing

`SunsetDate` is optional. If not set, only the `Deprecation` header is emitted.

### `Link` header missing

Either `SunsetLink` is `null`, or the value is not a valid absolute URI (logged as a warning).

## References

- [RFC 8594: The Sunset HTTP Header Field](https://www.rfc-editor.org/rfc/rfc8594) — specification for the `Sunset` response header
- [IETF HTTP Deprecation Header (draft)](https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-deprecation-header) — specification for the `Deprecation` response header
- [API versioning in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/web-api/advanced/conventions) — ASP.NET Core API conventions and versioning strategies
- [Asp.Versioning (formerly Microsoft.AspNetCore.Mvc.Versioning)](https://github.com/dotnet/aspnet-api-versioning) — the versioning library used by `ApiPipeline.NET.Versioning`

## Related

- [RUNBOOK.md](../RUNBOOK.md) — handling deprecated version traffic
- [ARCHITECTURE.md](../ARCHITECTURE.md) — middleware ordering
