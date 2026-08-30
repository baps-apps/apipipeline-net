# ApiPipeline.NET Sample

An API wired as the docs recommend: error handling with correlation IDs, OpenTelemetry with the
package's meter, a correlation-forwarding `HttpClient`, a versioned controller with a deprecated
v1, and an endpoint filter. It references the projects; your app should reference the packages.

## Run it

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project samples/ApiPipeline.NET.Sample
```

It listens on `http://localhost:5000`. Without `ASPNETCORE_ENVIRONMENT` it runs as Production
(strict CORS, trusted proxy CIDR, HSTS).

| Request | Shows |
| --- | --- |
| `curl -i http://localhost:5000/health` | `X-Correlation-Id`, `X-Trace-Id`, security headers |
| `curl -i -H "X-Correlation-Id: my-id" http://localhost:5000/whoami` | Your ID echoed back |
| `curl -i http://localhost:5000/api/v1/orders` | Deprecation headers |
| `curl -i http://localhost:5000/api/v2/orders` | No deprecation headers |
| `curl -i http://localhost:5000/tenanted/ping` | 400 from the endpoint filter (send `X-Tenant-Id`) |
| Many fast requests | 429 with `Retry-After` |

The console prints metrics and traces.

## Files

| File | Shows |
| --- | --- |
| `Program.cs` | The full wiring, commented |
| `Controllers/OrdersController.cs` | `[ApiVersion("1.0", Deprecated = true)]` |
| `SampleTenantHeaderFilter.cs` | A request check as an `IEndpointFilter` |
| `appsettings.json` | Shared settings |
| `appsettings.Development.json` | Allow-all CORS, no HSTS, `permissive` limit, compression and output caching on |
| `appsettings.Production.json` | Strict CORS, trusted proxy, higher limits; restates policies in base order ([why](../../docs/features/rate-limiting.md#changing-configuration)) |
| `ConfigurationSnippets/` | [Fragments](ConfigurationSnippets/README.md) to copy |

`ForwardedHeaders:KnownNetworks` (`10.0.10.0/24`) is a placeholder: use your ingress's own addresses.
Override any value with environment variables, for example `ApiPipeline__RateLimiting__Enabled=false`.
Options: [feature guides](../../README.md#features-and-defaults).
