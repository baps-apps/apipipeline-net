# Security Headers

`WithSecurityHeaders()` adds these headers. They are written just before the response starts, so
401, 403, 429, 504 and preflight responses get them too; a header your app already set is never
overwritten.

| Header | Default |
| --- | --- |
| `X-Content-Type-Options: nosniff` | on |
| `Referrer-Policy: no-referrer` | on |
| `X-Frame-Options: DENY` | on |
| `Strict-Transport-Security` | on, HTTPS only, not in Development |
| `Cross-Origin-Resource-Policy: same-origin` | on; other sites can't embed responses, CORS unaffected |
| `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` | on |
| `Cache-Control: no-store` | on, when the response sets none |
| `Permissions-Policy` | off |

## Setup

```csharp
builder.Services.AddApiPipeline(builder.Configuration);

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()   // HSTS behind a TLS-terminating proxy
    .WithSecurityHeaders());
```

No configuration needed: `Enabled` defaults to `true`.

## Options (section `ApiPipeline:SecurityHeaders`)

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | |
| `ReferrerPolicy` | `no-referrer` | `""` omits |
| `CacheControl` | `no-store` | When the response sets none; `""` omits |
| `AddXContentTypeOptionsNoSniff` | `true` | |
| `AddXFrameOptions` | `true` | |
| `XFrameOptionsValue` | `DENY` | `DENY` or `SAMEORIGIN` |
| `CrossOriginResourcePolicy` | `same-origin` | `same-origin`, `same-site`, `cross-origin`; `""` omits |
| `ContentSecurityPolicy` | `default-src 'none'; frame-ancestors 'none'` | `""` omits |
| `PermissionsPolicy` | none | e.g. `camera=(), microphone=(), geolocation=()` |
| `EnableStrictTransportSecurity` | `true` | |
| `StrictTransportSecurityMaxAge` | `365.00:00:00` | 0 to 3650 days |
| `StrictTransportSecurityIncludeSubDomains` | `true` | |
| `StrictTransportSecurityPreload` | `false` | Requires `IncludeSubDomains` and a max-age of a year or more |
| `StrictTransportSecurityExcludeLoopbackHosts` | `true` | Never to `localhost`, `127.0.0.1`, `[::1]` |
| `StrictTransportSecurityExcludedHosts` | none | Extra hosts, port ignored |

Use `""`, not `null`, to omit a header.

## HSTS

Written by ASP.NET Core's `UseHsts()`, only outside Development, on requests the app sees as HTTPS
(behind TLS termination that needs [forwarded headers](forwarded-headers.md)), and not to excluded
hosts. Before enabling `preload`, make sure every subdomain serves HTTPS (leaving the list takes
months) and submit at [hstspreload.org](https://hstspreload.org).

## Content-Security-Policy and HTML pages

The default CSP (OWASP's API recommendation) also blocks pages the API serves: Swagger UI, Scalar,
static files. Give only those paths their own policy, before the middleware that serves them:

```csharp
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/swagger"),
    branch => branch.Use((context, next) =>
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; " +
            "style-src 'self' 'unsafe-inline'; img-src 'self' data:";
        return next(context);
    }));

app.UseSwagger();
app.UseSwaggerUI();
```

Changing `ContentSecurityPolicy` instead loosens it for every API response.

## `Cache-Control: no-store` by default

Stops shared caches (CDNs, proxies) from storing authenticated responses (OWASP). It is only a
default: an endpoint meant to be cached sets its own value, for example
`[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]`. Output caching is
server-side and unaffected. Change it with `CacheControl`.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| No headers | `WithSecurityHeaders()` called, `Enabled` true, `SecurityHeaders` in the `pipeline composed` log |
| Wrong value | Your app set it first |
| No HSTS | Development, HTTP as the app sees it (forwarded headers), or an excluded host |
| Swagger UI blank or unstyled | [Give it its own CSP](#content-security-policy-and-html-pages) |

```bash
curl -sD - -o /dev/null https://api.example.com/health | grep -iE "x-content|referrer|strict-transport|x-frame|content-security|permissions"
```

## References

- [OWASP HTTP Headers Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/HTTP_Headers_Cheat_Sheet.html)
- [ASP.NET Core HSTS](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl#http-strict-transport-security-protocol-hsts)
