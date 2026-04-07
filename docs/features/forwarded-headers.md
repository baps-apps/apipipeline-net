# Forwarded Headers for Proxy/Ingress Deployments

## What it does

ApiPipeline.NET configures ASP.NET Core's forwarded headers middleware to correctly process `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` headers from trusted reverse proxies. This ensures the application sees the real client IP address, the original request scheme (HTTP/HTTPS), and the original host — critical for rate limiting, logging, HTTPS redirection, and audit trails.

## Why it matters

- **Correct client identity**: without forwarded headers, `RemoteIpAddress` is the proxy IP, not the client IP. This breaks rate limiting (all users share one bucket), logging, and geo-IP.
- **Correct scheme detection**: without `X-Forwarded-Proto` processing, HTTPS requests terminated at the proxy appear as HTTP to the app, breaking HTTPS redirection and secure cookie handling.
- **Fail-fast safety**: `EnforceTrustedProxyConfigurationInProduction` causes startup failure when proxy trust is not configured — preventing silent fallback to unsafe defaults.
- **Server fingerprinting prevention**: `SuppressServerHeader` removes the `Server: Kestrel` header from responses.

## How it works

```text
Client → Reverse Proxy → Application
         X-Forwarded-For: 203.0.113.50
         X-Forwarded-Proto: https
         X-Forwarded-Host: api.example.com

Without forwarded headers:
  RemoteIpAddress = 10.0.0.5 (proxy IP)
  Request.Scheme = http
  Request.Host = internal-hostname

With forwarded headers:
  RemoteIpAddress = 203.0.113.50 (real client IP)
  Request.Scheme = https
  Request.Host = api.example.com
```

### Proxy trust model

ASP.NET Core only processes forwarded headers from **trusted** proxies. The trust is configured via:

| Setting | Purpose |
|---|---|
| `KnownProxies` | Specific IP addresses of trusted proxies |
| `KnownNetworks` | CIDR ranges of trusted proxy networks (e.g., `10.0.0.0/8`) |
| `ClearDefaultProxies` | When `true`, clears the default loopback trust and uses only your configured proxies/networks |
| `ForwardLimit` | Maximum number of proxy hops to process |

### Common proxy topologies

```text
Single reverse proxy (NGINX, HAProxy):
  ForwardLimit: 1
  KnownProxies: ["10.0.0.5"] or KnownNetworks: ["10.0.0.0/24"]

Kubernetes NGINX Ingress:
  ForwardLimit: 2
  KnownNetworks: ["10.0.0.0/8"]
  ClearDefaultProxies: true

CloudFront → ALB → NGINX → Pod:
  ForwardLimit: 3–4
  KnownNetworks: ["10.0.0.0/8", "172.16.0.0/12"]
  ClearDefaultProxies: true
```

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddForwardedHeaders(builder.Configuration);
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()   // MUST be first — before anything reads IP or scheme
    .WithCorrelationId()
    .WithExceptionHandler()
    // ...
);
```

Forwarded headers **must** be the first middleware in the pipeline. Everything downstream depends on the resolved IP and scheme.

Alternatively, call it outside the builder for pre-pipeline processing:

```csharp
app.UseApiPipelineForwardedHeaders();  // outside UseApiPipeline
app.UseRequestSizeTracking();          // needs real IP context

app.UseApiPipeline(pipeline => pipeline
    .WithCorrelationId()
    // ... (omit WithForwardedHeaders since it's already applied)
);
```

## All available options

### `ForwardedHeadersSettings` (JSON section: `ForwardedHeadersOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Master switch. When `false`, forwarded headers middleware is skipped entirely. |
| `ForwardLimit` | `int` | `1` | Maximum proxy hops to process from `X-Forwarded-For`. Set to the number of trusted proxies. Range: 1–20. |
| `KnownProxies` | `string[]?` | `null` | IP addresses of trusted proxies (e.g., `["10.0.0.1"]`). |
| `KnownNetworks` | `string[]?` | `null` | CIDR ranges of trusted proxy networks (e.g., `["10.0.0.0/8"]`). |
| `ClearDefaultProxies` | `bool` | `false` | Clear default loopback trust. **Required** in most cloud/container deployments. |
| `EnforceTrustedProxyConfigurationInProduction` | `bool` | `true` | Fail startup in production if no proxies/networks configured and `ClearDefaultProxies` is `false`. |
| `SuppressServerHeader` | `bool` | `true` | When `true`, suppresses the `Server: Kestrel` response header to prevent server fingerprinting. Applied via `ConfigureKestrelOptions`. |

### Processed headers

The middleware processes these standard headers:

| Header | What it sets |
|---|---|
| `X-Forwarded-For` | `HttpContext.Connection.RemoteIpAddress` |
| `X-Forwarded-Proto` | `HttpContext.Request.Scheme` |
| `X-Forwarded-Host` | `HttpContext.Request.Host` |

## Configuration examples

### Local development (no proxy)

```json
{
  "ForwardedHeadersOptions": {
    "Enabled": false
  }
}
```

### Single reverse proxy

```json
{
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ForwardLimit": 1,
    "KnownProxies": ["10.0.0.5"],
    "KnownNetworks": [],
    "ClearDefaultProxies": true,
    "EnforceTrustedProxyConfigurationInProduction": true,
    "SuppressServerHeader": true
  }
}
```

### Kubernetes NGINX Ingress

```json
{
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ForwardLimit": 2,
    "KnownProxies": [],
    "KnownNetworks": ["10.0.0.0/8"],
    "ClearDefaultProxies": true,
    "EnforceTrustedProxyConfigurationInProduction": true,
    "SuppressServerHeader": true
  }
}
```

### Multi-layer proxy (CDN → Load Balancer → Ingress → Pod)

```json
{
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ForwardLimit": 4,
    "KnownProxies": [],
    "KnownNetworks": ["10.0.0.0/8", "172.16.0.0/12"],
    "ClearDefaultProxies": true,
    "EnforceTrustedProxyConfigurationInProduction": true,
    "SuppressServerHeader": true
  }
}
```

### Temporary opt-out of production enforcement

```json
{
  "ForwardedHeadersOptions": {
    "Enabled": true,
    "ForwardLimit": 1,
    "KnownProxies": [],
    "KnownNetworks": [],
    "ClearDefaultProxies": false,
    "EnforceTrustedProxyConfigurationInProduction": false,
    "SuppressServerHeader": true
  }
}
```

**Not recommended** — this trusts only loopback, so `X-Forwarded-For` from real proxies is ignored.

## Production recommendations

| Recommendation | Why |
|---|---|
| Always set `ClearDefaultProxies: true` in cloud/container environments | Default loopback trust doesn't match real proxy IPs |
| Configure `KnownNetworks` with your ingress CIDR | Only trust headers from your actual proxies |
| Set `ForwardLimit` to your actual proxy depth | Prevents IP spoofing through extra `X-Forwarded-For` entries |
| Keep `EnforceTrustedProxyConfigurationInProduction: true` | Prevents deployment without proxy trust configured |
| Set `SuppressServerHeader: true` | Prevents server fingerprinting |
| Place forwarded headers first in the pipeline | All downstream middleware depends on resolved IP/scheme |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Set `Enabled: false` for local development without a proxy | No proxy means no forwarded headers to process |
| Set `Enabled: true` in staging if staging has a proxy | Match production behavior |
| Test with `X-Forwarded-For` spoofing | Verify your trust config rejects untrusted sources |

## Troubleshooting

### `RemoteIpAddress` shows proxy IP instead of client IP

1. Verify `WithForwardedHeaders()` is first in the pipeline.
2. Check `ClearDefaultProxies: true` for non-loopback proxies.
3. Verify `KnownNetworks` or `KnownProxies` includes your proxy's IP/CIDR.
4. Check `ForwardLimit` matches your proxy depth.

```bash
# Verify X-Forwarded-For is being sent by your proxy
curl -v https://api.example.com/health 2>&1 | grep -i "x-forwarded"
```

### Startup failure: production trust enforcement

```text
ForwardedHeaders: Production environment requires trusted proxy configuration.
Configure KnownProxies or KnownNetworks, or set ClearDefaultProxies to true.
```

Fix: set `ClearDefaultProxies: true` and configure `KnownNetworks` for your environment, or set `EnforceTrustedProxyConfigurationInProduction: false` (not recommended).

### HTTPS redirection loops

The app sees `Request.Scheme` as `http` even though the client used `https`. This means `X-Forwarded-Proto` is not being processed:

1. Verify forwarded headers middleware is enabled and runs first.
2. Verify your proxy sends `X-Forwarded-Proto: https`.
3. Check proxy trust configuration.

### Rate limiting puts all users in one bucket

All requests share the proxy's IP as the partition key. Fix the forwarded headers configuration so `RemoteIpAddress` is the real client IP. See [rate-limiting.md](rate-limiting.md).

### `Server: Kestrel` header still present

Verify that `ForwardedHeadersOptions:SuppressServerHeader` is set to `true` (the default) and that `AddRequestLimits` or `AddApiPipeline` has been called during service registration. The `ConfigureKestrelOptions` class applies `AddServerHeader = false` when this setting is enabled.

## References

- [Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer) — Microsoft's forwarded headers middleware documentation
- [ForwardedHeadersOptions class reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.builder.forwardedheadersoptions) — all available properties for `ForwardedHeadersOptions`
- [MDN: X-Forwarded-For](https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/X-Forwarded-For) — specification and browser/proxy behavior for the `X-Forwarded-For` header

## Related

- [rate-limiting.md](rate-limiting.md) — rate limiting depends on correct IP resolution
- [RUNBOOK.md](../RUNBOOK.md) — incident response for incorrect client IP
- [OPERATIONS.md](../OPERATIONS.md) — production proxy configuration
