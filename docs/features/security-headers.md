# Security Headers

## What it does

ApiPipeline.NET's `SecurityHeadersMiddleware` applies HTTP security response headers that instruct browsers and intermediaries to enforce security policies. Headers are set via `OnStarting` callbacks, ensuring they are applied even when downstream components write directly to the response.

## Why it matters

- **OWASP compliance**: covers the recommended response headers from the OWASP Secure Headers Project.
- **Defense in depth**: headers like `X-Frame-Options` and `CSP` protect against clickjacking, XSS, and content-type confusion — even if application code has vulnerabilities.
- **Development safety**: HSTS is automatically skipped in Development environments, preventing accidental HTTPS-only lockout during local development.
- **Hot-reload**: uses `IOptionsMonitor` so header configuration changes take effect without app restart (when config reload is supported).

## How it works

The middleware registers an `OnStarting` callback that applies headers before the response body is sent. This guarantees headers are set regardless of how downstream middleware or endpoints write their responses.

```text
Request → Pipeline → Endpoint writes response
                       ↓
                   OnStarting callback fires
                       ↓
                   Security headers applied to response
                       ↓
                   Response sent to client
```

Headers are only set if they are not already present, so downstream middleware or endpoint code can override specific headers when needed.

## Registration

```csharp
// Aggregate (recommended)
builder.Services.AddApiPipeline(builder.Configuration);

// Or standalone
builder.Services.AddSecurityHeaders(builder.Configuration);
```

### Pipeline

```csharp
app.UseApiPipeline(pipeline => pipeline
    // ...
    .WithSecurityHeaders()
    // ...
);
```

## All available options

### `SecurityHeadersSettings` (JSON section: `SecurityHeadersOptions`)

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch. When `false`, no security headers are applied. |
| `ReferrerPolicy` | `string?` | `"no-referrer"` | Value of the `Referrer-Policy` header. Controls referrer information sent with requests. |
| `AddXContentTypeOptionsNoSniff` | `bool` | `true` | Adds `X-Content-Type-Options: nosniff`. Prevents MIME-sniffing attacks. |
| `EnableStrictTransportSecurity` | `bool` | `true` | Adds `Strict-Transport-Security` (HSTS). Automatically skipped in Development. |
| `StrictTransportSecurityMaxAgeSeconds` | `int` | `31536000` | HSTS `max-age` value in seconds. Default is 1 year. Valid range: 0–2147483647. |
| `StrictTransportSecurityIncludeSubDomains` | `bool` | `true` | Appends `includeSubDomains` to the HSTS header. |
| `StrictTransportSecurityPreload` | `bool` | `false` | Appends `preload` to the HSTS header. Only enable when all subdomains support HTTPS. |
| `AddXFrameOptions` | `bool` | `true` | Adds `X-Frame-Options` header to prevent clickjacking. |
| `XFrameOptionsValue` | `string` | `"DENY"` | Value of `X-Frame-Options`. Valid values: `DENY`, `SAMEORIGIN`. |
| `ContentSecurityPolicy` | `string?` | `null` | Value of `Content-Security-Policy`. Set to `null` to omit. |
| `PermissionsPolicy` | `string?` | `null` | Value of `Permissions-Policy`. Set to `null` to omit. |

## Header reference

| Header | Purpose | When to use |
|---|---|---|
| `X-Content-Type-Options: nosniff` | Prevents browsers from MIME-sniffing response content | Always — essential for APIs |
| `Referrer-Policy: no-referrer` | Prevents browser from sending referrer information | Always — API calls shouldn't leak URLs |
| `Strict-Transport-Security` | Forces HTTPS for all future requests | When TLS is properly configured |
| `X-Frame-Options: DENY` | Prevents embedding in `<iframe>` | When API domain should not be framed |
| `Content-Security-Policy` | Controls which resources the browser can load | When API serves HTML/browser content |
| `Permissions-Policy` | Restricts browser APIs (camera, mic, geolocation) | When API domain is loaded in browser |

## Configuration examples

### API-only (no browser rendering)

```json
{
  "SecurityHeadersOptions": {
    "Enabled": true,
    "ReferrerPolicy": "no-referrer",
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "StrictTransportSecurityIncludeSubDomains": true,
    "StrictTransportSecurityPreload": false,
    "AddXFrameOptions": true,
    "XFrameOptionsValue": "DENY",
    "ContentSecurityPolicy": null,
    "PermissionsPolicy": null
  }
}
```

### API + Swagger UI / browser-rendered pages

```json
{
  "SecurityHeadersOptions": {
    "Enabled": true,
    "ReferrerPolicy": "strict-origin-when-cross-origin",
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "StrictTransportSecurityIncludeSubDomains": true,
    "StrictTransportSecurityPreload": false,
    "AddXFrameOptions": true,
    "XFrameOptionsValue": "SAMEORIGIN",
    "ContentSecurityPolicy": "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:",
    "PermissionsPolicy": "camera=(), microphone=(), geolocation=()"
  }
}
```

### HSTS preload-ready

```json
{
  "SecurityHeadersOptions": {
    "Enabled": true,
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 63072000,
    "StrictTransportSecurityIncludeSubDomains": true,
    "StrictTransportSecurityPreload": true
  }
}
```

Produces: `Strict-Transport-Security: max-age=63072000; includeSubDomains; preload`

Before enabling `preload`, verify:
1. All subdomains support HTTPS.
2. You understand that HSTS preload is difficult to reverse.
3. Submit your domain to [hstspreload.org](https://hstspreload.org) after deploying.

### Minimal (disable most headers)

```json
{
  "SecurityHeadersOptions": {
    "Enabled": true,
    "ReferrerPolicy": null,
    "AddXContentTypeOptionsNoSniff": true,
    "EnableStrictTransportSecurity": false,
    "AddXFrameOptions": false,
    "ContentSecurityPolicy": null,
    "PermissionsPolicy": null
  }
}
```

## Production recommendations

| Recommendation | Why |
|---|---|
| Always enable `X-Content-Type-Options: nosniff` | Prevents MIME-sniffing attacks on JSON responses |
| Enable HSTS when TLS is properly configured | Forces HTTPS, prevents downgrade attacks |
| Start with `StrictTransportSecurityPreload: false` | Preload is hard to reverse; enable after verification |
| Set `X-Frame-Options: DENY` for APIs | APIs should not be embeddable in iframes |
| Add CSP and Permissions-Policy when serving browser content | Swagger UI or portal pages need these |
| Use `Referrer-Policy: no-referrer` for APIs | API URLs may contain sensitive path segments |

## Non-production recommendations

| Recommendation | Why |
|---|---|
| Keep security headers enabled | Tests should validate header presence |
| HSTS is automatically skipped in Development | No action needed — prevents HTTPS lockout on localhost |
| Test with `Content-Security-Policy` if using Swagger | Catch CSP violations early in development |

## Troubleshooting

### Security headers missing from responses

1. Verify `SecurityHeadersOptions:Enabled` is `true`.
2. Verify `WithSecurityHeaders()` is called in `UseApiPipeline`.
3. Check if another middleware is overwriting headers.

```bash
curl -s -D - https://api.example.com/health | grep -iE "x-content-type|referrer-policy|strict-transport|x-frame-options|content-security-policy|permissions-policy"
```

### HSTS not present in response

HSTS is automatically skipped in Development environments. Verify in staging or production.

### Swagger UI broken after enabling CSP

Your CSP needs to allow Swagger's inline scripts and styles. Use:

```json
"ContentSecurityPolicy": "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:"
```

### Header already set by downstream middleware

`SecurityHeadersMiddleware` checks `headers.ContainsKey()` before setting each header. If another middleware sets a header first, the pipeline respects that value. This is intentional — it allows endpoint-specific overrides.

## References

- [OWASP Secure Headers Project](https://owasp.org/www-project-secure-headers/) — recommended security headers and best practices
- [OWASP HTTP Headers Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/HTTP_Headers_Cheat_Sheet.html) — header-by-header security guidance
- [MDN: Strict-Transport-Security](https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Strict-Transport-Security) — HSTS specification, `includeSubDomains`, and `preload`
- [MDN: Content-Security-Policy](https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Security-Policy) — CSP directives reference
- [HSTS Preload list submission](https://hstspreload.org) — requirements and submission for browser HSTS preloading

## Related

- [OPERATIONS.md](../OPERATIONS.md) — production security header baseline
- [ARCHITECTURE.md](../ARCHITECTURE.md) — middleware ordering
