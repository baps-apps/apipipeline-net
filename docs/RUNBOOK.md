# Runbook

Responses to the [alerts](OPERATIONS.md#monitoring). Causes and fixes are in the linked guides.

## Pods fail to start after a deploy

**P1.** `kubectl logs <pod> --previous`: the message names the setting
([common messages](TROUBLESHOOTING.md#the-app-fails-to-start)). Roll back, or fix the value and
redeploy.

## Rolling back an upgrade to 2.0.0

**Severity of the triggering incident.** Redeploy the previous build; don't downgrade the package
in place, since the 2.0.0 code changes don't compile against 1.1.0.

1. Check that the configuration the old build reads still exists: the top-level `<Feature>Options`
   sections, environment variables and secrets
   ([keep both shapes](migration.md#keep-both-configuration-shapes-until-the-rollout-is-done)).
   Missing, 1.1.0 starts on defaults: restore them first.
2. Roll back the deployment (`kubectl rollout undo deployment/<name>`, or redeploy the previous
   image).
3. Watch the 1.1.0 series return (`apipipeline_ratelimit_rejected_total`) and 5xx and latency hold.
   Shared limits become per pod; leftover Redis keys expire by themselves.
4. Tell client teams the `X-RateLimit-*` and `Deprecation` formats are back
   ([what changes](migration.md#what-changes-on-rollback)).

## Spike in 429s

**P2.**

```promql
sum by (apipipeline_rate_limiting_policy, apipipeline_rate_limiting_partition_type) (rate(apipipeline_rate_limiting_rejected_total[5m]))
rate(apipipeline_edge_rate_limiting_rejected_total[5m])
```

Mostly `ip` across unrelated clients means [forwarded headers](features/forwarded-headers.md#troubleshooting)
are wrong. Check recent `ApiPipeline:RateLimiting` changes. Mitigate by raising `PermitLimit` (or the
edge `PermitLimit`) and rolling the deployment. Afterwards: size limits per pod from real traffic and
enforce the global limit at the ingress.

## Redis down: limits are per pod

**P3** (P2 if the limits protect something fragile). Requests succeed, but each pod limits alone.

Diagnose:

1. `apipipeline_rate_limiting_store_degraded == 1`; group
   `apipipeline_rate_limiting_store_fallback_total` by `…fallback_reason`: `timeout` (slow),
   `error` (failing), `circuit_open` (not tried). Log: `Shared rate limiting is unavailable` (event
   100).
2. Check Redis from a pod, and that `Redis:Configuration` points at the right host, credentials and
   TLS.
3. Fallbacks above 0 with `store.degraded` at 0: one bad key. Enable Debug for
   `ApiPipeline.NET.RateLimiting.Redis.RedisCircuit` (event 102), then delete the key whose type
   doesn't match its kind (fixed window: string; sliding window and token bucket: hash;
   concurrency: zset), or give colliding applications distinct `KeyPrefix`es.

Mitigate: restore Redis; pods rejoin within `FallbackCooldown` (event 101). If N× the limit is
unsafe meanwhile, lower `PermitLimit` and roll. After a long outage, concurrency slots free within
`ConcurrencyLeaseTimeout`. Limits resetting without an outage: eviction (event 105), use
`noeviction`.

## CORS failures from browsers

**P2.** The browser console names the origin, method or header. A rise in
`apipipeline_cors_rejected_total` after a deploy usually means an origin was dropped. Add it to
`ApiPipeline:Cors` and roll; see [cors.md](features/cors.md#troubleshooting).

## Wrong client IP

**P1**: limits and audit logs are unreliable. Set `KnownNetworks` to the ingress's addresses,
`ClearDefaultProxies: true`, `ForwardLimit` to the number of proxies, and roll
([guide](features/forwarded-headers.md#troubleshooting)).

## Repeated 5xx

**P1.**

- **504**: the request timeout fired; find the slow dependency, or `.DisableRequestTimeout()` on a
  streaming endpoint, or raise `DefaultTimeout`.
- **500**: an unhandled exception. Follow one failure by its `X-Correlation-Id` (logs) or
  `X-Trace-Id` (traces). Without the [error handling setup](../README.md#error-handling), bodies and
  log lines lack the IDs.

Roll back the change that caused it.

## Deprecated version still used

**P3.** Measure traffic with
`sum by (http_route) (rate(http_server_request_duration_seconds_count{http_route=~"/api/v1.*"}[1h]))`.
Check the headers on a request that reaches the endpoint (401s, 429s and preflights have none):
`curl -sD - -o /dev/null https://api.example.com/api/v1/resource | grep -iE "deprecation|sunset|link"`.
Missing: see [version-deprecation.md](features/version-deprecation.md#troubleshooting). Tell consumers
the timeline; date changes need a restart.
