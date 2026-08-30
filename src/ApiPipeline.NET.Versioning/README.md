# ApiPipeline.NET.Versioning

Deprecation and sunset dates from configuration for
[Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning), which writes the `Deprecation`
(RFC 9745), `Sunset` (RFC 8594) and `Link` headers. Depends only on `Asp.Versioning.Http`.

```bash
dotnet add package ApiPipeline.NET.Versioning
```

```csharp
builder.Services.AddApiVersioning(o =>
{
    o.ReportApiVersions = true;                               // required for the headers
    o.ApiVersionReader = new UrlSegmentApiVersionReader();    // the reader your API uses (AV0015 if unset)
});
builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);   // or (o => ...)
```

```json
{
  "ApiPipeline": {
    "VersionDeprecation": {
      "Enabled": true,
      "DeprecatedVersions": [
        {
          "Version": "1.0",
          "DeprecationDate": "2026-01-01T00:00:00Z",
          "SunsetDate": "2026-12-31T00:00:00Z",
          "DeprecationLink": "https://docs.example.com/v1-migration",
          "SunsetLink": "https://docs.example.com/v1-sunset"
        }
      ]
    }
  }
}
```

Registration order doesn't matter, invalid dates or links fail startup, and dates apply on restart.
Mark endpoints `Deprecated = true` for `api-deprecated-versions`.

Full guide: [version-deprecation.md](https://github.com/baps-apps/apipipeline-net/blob/main/docs/features/version-deprecation.md).
