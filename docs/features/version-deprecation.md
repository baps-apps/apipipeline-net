# API Version Deprecation Headers

`ApiPipeline.NET.Versioning` feeds deprecation dates and links from configuration to
[`Asp.Versioning`](https://github.com/dotnet/aspnet-api-versioning), which writes the headers:

```http
Deprecation: @1751328000
Sunset: Thu, 01 Jan 2026 00:00:00 GMT
Link: <https://docs.example.com/v1-migration>; rel="deprecation"; type="text/html"
Link: <https://docs.example.com/v1-sunset>; rel="sunset"; type="text/html"
api-deprecated-versions: 1.0
```

`Deprecation` follows [RFC 9745](https://www.rfc-editor.org/rfc/rfc9745.html), `Sunset`
[RFC 8594](https://www.rfc-editor.org/rfc/rfc8594.html).

## Setup

```bash
dotnet add package ApiPipeline.NET.Versioning   # depends only on Asp.Versioning.Http
```

```csharp
builder.Services
    .AddApiVersioning(o =>
    {
        o.DefaultApiVersion = new ApiVersion(1, 0);
        o.ReportApiVersions = true;                 // required
        o.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddMvc();                                      // controllers only (Asp.Versioning.Mvc)

builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);   // either order
```

```json
{
  "ApiPipeline": {
    "VersionDeprecation": {
      "Enabled": true,
      "DeprecatedVersions": [
        {
          "Version": "1.0",
          "DeprecationDate": "2025-07-01T00:00:00Z",
          "SunsetDate": "2026-01-01T00:00:00Z",
          "DeprecationLink": "https://docs.example.com/v1-migration",
          "SunsetLink": "https://docs.example.com/v1-sunset"
        }
      ]
    }
  }
}
```

No `With*()` call is needed. Mark the endpoint deprecated too (`[ApiVersion("1.0", Deprecated = true)]`
or `.HasDeprecatedApiVersion(...)`) for `api-deprecated-versions`. Reporting must be on, globally
or per version set (`.ReportApiVersions()`); with it off globally, startup warns (event 201).

## Options (section `ApiPipeline:VersionDeprecation`)

`Enabled` (default `false`) and `DeprecatedVersions`, each with:

| Property | Required | Notes |
| --- | --- | --- |
| `Version` | yes | As in `[ApiVersion]`, e.g. `"1.0"` |
| `DeprecationDate` | yes | `Deprecation` |
| `SunsetDate` | no | `Sunset`; not before `DeprecationDate` |
| `DeprecationLink` | no | Absolute URL, `rel="deprecation"` |
| `SunsetLink` | no | Absolute URL, `rel="sunset"`; needs `SunsetDate` |

Validated only while `Enabled`; invalid entries fail startup with a message naming the entry. Dates
apply at startup.

## Limitations

- Responses that never reach the endpoint (401, 403, 429, preflight) get no headers.
- No metric: filter `http.server.request.duration` by the deprecated routes.
- Versions are read by `Asp.Versioning`'s `ApiVersionReader`.

## Troubleshooting

Headers missing? Check that `AddApiPipelineVersionDeprecation` is called, reporting is on, `Enabled`
is true with the version listed, the app restarted after a date change, the endpoint is marked
deprecated (for `api-deprecated-versions`), and the request reached the endpoint.

```bash
curl -sD - -o /dev/null https://api.example.com/api/v1/orders | grep -iE "deprecation|sunset|link|api-.*versions"
```

Upgrading from 1.x: [migration](../migration.md#2g-api-version-deprecation-moved-to-its-own-package).
