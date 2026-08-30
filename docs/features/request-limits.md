# Request Limits (Kestrel, IIS, HTTP.sys and Forms)

Server and form limits from one section, applied at startup (no `With*()` call): size limits,
slow-client limits, and removal of `Server: Kestrel`, which applies even when disabled.

## Setup

```json
{
  "ApiPipeline": {
    "RequestLimits": {
      "Enabled": true,
      "MaxRequestBodySize": 10485760,
      "RequestHeadersTimeout": "00:00:15",
      "MaxConcurrentConnections": 2000,
      "MinRequestBodyDataRateBytesPerSecond": 240
    }
  }
}
```

Unset values keep the server's defaults. A limit your app sets on `KestrelServerOptions`,
`IISServerOptions`, `HttpSysOptions` or `FormOptions` wins
([details](../ARCHITECTURE.md#framework-options-your-settings-win)). Kestrel doesn't read
`Kestrel:Limits` itself: use this section, or bind `Kestrel` with
`Configure<KestrelServerOptions>(configuration.GetSection("Kestrel"))`.

## Options (section `ApiPipeline:RequestLimits`)

| Property | Server default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | |
| `SuppressServerHeader` | `true` | Applies even when disabled |
| `MaxRequestBodySize` | ~30 MB | Bytes, ≥ 1; also the form body limits |
| `MaxRequestHeadersTotalSize` | 32 KB | |
| `MaxRequestHeaderCount` | 100 | |
| `MaxFormValueCount` | 1024 | |
| `RequestHeadersTimeout` | `00:00:30` | 1 s–1 h |
| `KeepAliveTimeout` | `00:02:10` | 1 s–1 h |
| `MaxConcurrentConnections` | unlimited | Size to the pod |
| `MinRequestBodyDataRateBytesPerSecond` | 240 | `0` turns the check off |
| `MinRequestBodyDataRateGracePeriod` | `00:00:05` | Over 1 s, at most 1 h; only with a rate |

Keep `MaxRequestBodySize` at or below the ingress limit. Raise limits per endpoint instead of
globally:

```csharp
[RequestSizeLimit(104_857_600)]                              // or [DisableRequestSizeLimit]
[RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]  // multipart needs both
public IActionResult Upload(IFormFile file) => Ok();

app.MapPost("/api/uploads", Upload).WithMetadata(new RequestSizeLimitAttribute(104_857_600));
```

## Under IIS or HTTP.sys

These servers don't read Kestrel's options; each gets what it supports, and a startup warning
(event 70) lists the rest. IIS out-of-process uses Kestrel, so everything applies.

| Setting | IIS in-process | HTTP.sys |
| --- | --- | --- |
| `MaxRequestBodySize`, `MaxFormValueCount` | Yes | Yes |
| `MaxConcurrentConnections` | No (site limits) | Yes |
| `RequestHeadersTimeout`, `KeepAliveTimeout` | No (`connectionTimeout`) | Yes |
| Header size and count | No (`requestLimits` in web.config) | No (registry) |
| `MinRequestBodyDataRateBytesPerSecond` | No | No |

Source-built .NET runtimes (Homebrew, some Linux distribution packages) ship the IIS and HTTP.sys
assemblies as empty stubs, so those servers can't run there at all. The package skips their
settings on such a runtime and the application starts normally on Kestrel, with Kestrel and form
limits applied.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| `413 Payload Too Large` | Body over `MaxRequestBodySize`; raise it per endpoint |
| "Form value count limit exceeded" | More fields than `MaxFormValueCount` |
| Multipart upload fails below the body limit | Also raise `RequestFormLimits` |
| "Request body too slow" | Lower `MinRequestBodyDataRateBytesPerSecond`, or `0` |
| `Server: Kestrel` present | `SuppressServerHeader: false`, or the ingress adds it |

## References

- [Kestrel options](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options)
