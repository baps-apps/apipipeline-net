# ApiPipeline.NET

A shared ASP.NET Core middleware pipeline for APIs: rate limiting, security headers, CORS,
correlation IDs, forwarded headers, compression, output caching, request limits and timeouts, with
configuration validated at startup. .NET 10, trimming and Native AOT compatible.

```bash
dotnet add package ApiPipeline.NET
```

```csharp
builder.Services.AddApiPipeline(builder.Configuration);   // reads "ApiPipeline"
builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithSecurityHeaders()
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting());
```

```json
{
  "ApiPipeline": {
    "ForwardedHeaders": { "KnownNetworks": [ "10.0.10.0/24" ], "ClearDefaultProxies": true },
    "Cors": { "Enabled": true, "AllowedOrigins": [ "https://app.example.com" ] },
    "RateLimiting": {
      "Enabled": true,
      "DefaultPolicy": "standard",
      "Policies": [ { "Name": "standard", "Kind": "FixedWindow", "PermitLimit": 100, "Window": "00:01:00" } ]
    }
  }
}
```

A feature runs when its `With*()` is called and its `Enabled` is `true` (correlation IDs, security
and forwarded headers are on by default). Outside Development, startup fails until forwarded headers
trust your ingress.

Optional: [ApiPipeline.NET.Versioning](https://github.com/baps-apps/apipipeline-net/blob/main/src/ApiPipeline.NET.Versioning/README.md),
[ApiPipeline.NET.RateLimiting.Redis](https://github.com/baps-apps/apipipeline-net/blob/main/src/ApiPipeline.NET.RateLimiting.Redis/README.md).
Documentation: [README](https://github.com/baps-apps/apipipeline-net/blob/main/README.md) ·
[migration](https://github.com/baps-apps/apipipeline-net/blob/main/docs/migration.md) ·
[changelog](https://github.com/baps-apps/apipipeline-net/blob/main/CHANGELOG.md).
