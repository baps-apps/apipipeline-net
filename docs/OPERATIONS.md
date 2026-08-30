# Operations Guide

Running a service that uses `ApiPipeline.NET` in Kubernetes. Alerts: [runbook](RUNBOOK.md).
Problems: [troubleshooting](TROUBLESHOOTING.md).

## Production settings

Start from the README's [configuration](../README.md#configuration) and
[checklist](../README.md#production-checklist). In Kubernetes also:

- **Trust only the ingress**: its own addresses as the pod sees them, not the pod CIDR.
- **Limits are per pod**, so totals move with the HPA. Enforce global limits at the ingress or use
  Redis.
- **Redis**: same zone as the pods, `maxmemory-policy noeviction`, a `KeyPrefix` per API and
  environment, `PartitionKeySecret` from a secret store, sized for
  [calls per request](features/rate-limiting.md#load-on-redis). `PolicyFallbacks: Reject` for every
  abuse-sensitive policy (login, OTP, payments): the default `PerPod` allows N× the limit while
  Redis is down ([choosing](features/rate-limiting.md#when-redis-is-unavailable)).
- **Match the ingress**: `MaxRequestBodySize` ≤ its body limit, `DefaultTimeout` < its timeout.
- **Compression behind TLS termination** needs `EnableForHttps`, or let the ingress compress.

## Changing configuration

Settings are read at startup: edit the ConfigMap, then `kubectl rollout restart`. An invalid value
fails the new pods before they serve, so the rollout stalls while old pods keep serving. Test
proxy trust, rate limits and `PartitionKeySecret` (which resets buckets) in staging first.

## Health checks

`/health` and `/internal/health`, and everything under them, are exempt from compression and, for
`GET`/`HEAD` from `ProbeNetworks` without proxy headers, from both rate limiters
([details](features/rate-limiting.md#health-probes)); not from request timeouts. A configured
`ExcludedPaths` replaces the default. Keep probes cheap: they are never limited.

```yaml
livenessProbe:
  httpGet: { path: /health/live, port: 8080 }
readinessProbe:
  httpGet: { path: /health/ready, port: 8080 }
```

## Monitoring

Export the meter ([OBSERVABILITY.md](OBSERVABILITY.md)). Responses are in the [runbook](RUNBOOK.md).

| Signal | Prometheus query | Alert |
| --- | --- | --- |
| Main limiter 429s | `rate(apipipeline_rate_limiting_rejected_total[5m])` | Sustained rise |
| Edge limiter 429s | `rate(apipipeline_edge_rate_limiting_rejected_total[5m])` | Sudden spike |
| Partition cap reached | `rate(apipipeline_rate_limiting_partitions_overflow_total[5m])` (and the edge counter) | `> 0` for 5 min |
| Redis degraded | `max(apipipeline_rate_limiting_store_degraded)` | `== 1` for 5 min |
| Redis latency | `histogram_quantile(0.99, sum by (le) (rate(apipipeline_rate_limiting_store_duration_seconds_bucket[5m])))` | > half of `CommandTimeout` |
| Redis fallbacks | `sum by (apipipeline_rate_limiting_policy) (rate(apipipeline_rate_limiting_store_fallback_total[5m]))` | Dashboard; > 0 means per pod |
| CORS rejections | `rate(apipipeline_cors_rejected_total[5m])` | Spike after a deploy |
| Invalid correlation IDs | `rate(apipipeline_correlation_id_rejected_total[5m])` | Sustained |
| 5xx | `rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m])` | > 1% of traffic |
| p95 latency | `histogram_quantile(0.95, rate(http_server_request_duration_seconds_bucket[5m]))` | > 1.5 × baseline |
| Resolver failures | Log event 23 | Any |
| Redis can evict keys | Log event 105 | Any |
| Redis without TLS | Log event 107 | Any |

With the Redis package, don't alert on `aspnetcore.rate_limiting.queued_requests`
([why](OBSERVABILITY.md#dashboards-and-alerts-with-the-redis-package)).

## Before each rollout

- Build and tests pass.
- `UseApiPipeline` calls `.RequireSecurityFeaturesEnabled()`, so a security feature left off fails
  startup instead of shipping disabled.
- Shared (Redis) limits: in staging, under load, `store.duration` p99 is well below
  `CommandTimeout` and `store.fallback` stays near zero; the connection uses TLS (no event 107) and
  a least-privilege user ([TLS and credentials](features/rate-limiting.md#tls-and-credentials)).
- `KnownNetworks`/`KnownProxies` match the ingress (otherwise pods won't start).
- Ingress limits match `MaxRequestBodySize` and `DefaultTimeout`.
- The `pipeline composed` log line lists the expected phases.
