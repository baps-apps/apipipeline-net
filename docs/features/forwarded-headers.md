# Forwarded Headers

Behind a proxy the app sees the proxy's IP and, with TLS terminated there, `http`.
`WithForwardedHeaders()` restores the client IP and scheme from `X-Forwarded-For` and
`X-Forwarded-Proto`, from trusted proxies only. Rate limiting, HSTS, HTTPS redirection and logs
depend on it. `X-Forwarded-Host` is ignored unless [`ProcessHost`](#processhost-and-allowedhosts) is
set.

## Setup

```json
{
  "ApiPipeline": {
    "ForwardedHeaders": {
      "ForwardLimit": 1,
      "KnownNetworks": [ "10.0.10.0/24" ],
      "ClearDefaultProxies": true
    }
  }
}
```

```csharp
builder.Services.AddApiPipeline(builder.Configuration);

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()   // first
    .WithCorrelationId());
```

`Enabled` defaults to `true`.

## The startup safety check

Outside Development, startup fails for an **empty** trust set (the framework would believe any
caller), a **loopback-only** one (the real proxy is ignored and all clients share one bucket), or a
`KnownNetworks` set that **contains every client address**: `0.0.0.0/0`, `::/0`, an IPv6 range
holding `::ffff:0:0/96` (the form dual-stack sockets give IPv4 clients), or entries that do so only
together, such as `0.0.0.0/1` and `128.0.0.0/1`. Development only warns. Headers are applied only to
connections with a peer address (not Unix sockets).

A `KnownProxies` or `KnownNetworks` entry that isn't an address or CIDR range fails startup in every
environment, including Development. Write IPv4 as four decimal numbers: the framework would read
`10.0.10` as `10.0.0.10`, `010.0.0.1` as `8.0.0.1` and `10/8` as `0.0.0.0/8`, so these are
rejected too. `ProbeNetworks` follows the same rule.

## Options (section `ApiPipeline:ForwardedHeaders`)

| Property | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | |
| `ForwardLimit` | `1` | Number of proxies in front of the app (1–20) |
| `KnownProxies` | none | Trusted proxy IPs |
| `KnownNetworks` | none | Trusted CIDR ranges; host bits masked |
| `ClearDefaultProxies` | `false` | Drop the built-in loopback trust; use with the lists above |
| `EnforceTrustedProxyConfiguration` | `true` | The startup check |
| `ProcessHost` | `false` | Apply `X-Forwarded-Host` |

**Trust only your proxies' addresses**, as the pod sees them. Every trusted address can set the
client IP, so a whole private range lets any workload in it pick its own. `ForwardLimit` above the
number of proxies lets callers inject `X-Forwarded-For` entries; below it you get a proxy's IP.

| Topology | Settings |
| --- | --- |
| One reverse proxy | `KnownProxies: ["<proxy IP>"]`, `ClearDefaultProxies: true` |
| Kubernetes ingress | `KnownNetworks`: the ingress controller's addresses; `ClearDefaultProxies: true` |
| CDN → load balancer → ingress | `KnownNetworks` for every internal hop, `ForwardLimit: 3` or `4` |

## `ProcessHost` and `AllowedHosts`

Most ingresses copy the caller's `Host` into `X-Forwarded-Host`, so `ProcessHost` requires a real
`AllowedHosts` (not empty, not `*`) outside Development:

```json
{
  "AllowedHosts": "api.example.com;api.internal",
  "ApiPipeline": { "ForwardedHeaders": { "ProcessHost": true } }
}
```

Host filtering checks the connection's host (include every name clients and probes use, or they
get 400); the pipeline checks the forwarded host and ignores one not on the list.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Client IP is the proxy's; everyone shares one bucket | The proxy's address as the pod sees it is trusted; `ForwardLimit` |
| `KnownProxies entry '...' is not an IP address` (or `KnownNetworks`) | Fix the entry; IPv4 as four decimal numbers |
| `RemoteIpAddress` null, anonymous 429s | [Socket listener](rate-limiting.md#unix-sockets-and-named-pipes) |
| HTTPS redirect loop, no HSTS | Proxy sends `X-Forwarded-Proto: https` and is trusted |
| Startup error about the trusted-proxy set | Set `KnownNetworks`/`KnownProxies` |
| `KnownNetworks entry '...' contains every client address` (or `entries ... together contain`) | List only your proxies' ranges |
| Startup error about `ProcessHost` | Set `AllowedHosts`, or `ProcessHost: false` |

## References

- [Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer)
