# Runbook

Operational runbook for incidents related to API pipeline behavior. Each alert includes severity, diagnosis steps, mitigation, and follow-up actions.

---

## Alert: spike in `429 Too Many Requests`

**Severity:** P2 (High) — clients are being throttled; potential revenue/availability impact.

### Diagnose

1. Identify impacted routes and client segments:

```promql
# Top rate-limited routes
topk(10, sum by (http_route) (rate(apipipeline_ratelimit_rejected_total[5m])))
```

2. Check whether forwarded headers are configured correctly — wrong IP identity can collapse all clients into one bucket:

```bash
# Verify X-Forwarded-For is being processed
curl -H "X-Forwarded-For: 203.0.113.50" https://api.example.com/health -v 2>&1 | grep -i x-forwarded
```

3. Inspect recent config changes to `RateLimitingOptions` (check deployment history or ConfigMap diffs).

4. Check partition key distribution:

```promql
# If all rejections share the same partition, proxy trust is likely misconfigured
sum by (partition_key) (rate(apipipeline_ratelimit_rejected_total[5m]))
```

### Mitigate

- Temporarily relax specific policy limits in config and restart/redeploy:

```json
{
  "RateLimitingOptions": {
    "Policies": [{ "Name": "strict", "PermitLimit": 200, "WindowSeconds": 60 }]
  }
}
```

- Apply named policy overrides to critical routes:

```csharp
app.MapGet("/api/critical", handler).RequireRateLimiting("permissive");
```

### Follow-up

- Tune quota with proper traffic analysis.
- Fix forwarded header config if partition collapse was the root cause.
- Add per-route named policies for endpoints with different traffic profiles.

---

## Alert: CORS failures from browser clients

**Severity:** P2 (High) — frontend users cannot reach the API.

### Diagnose

1. Check browser console for the exact CORS error (missing `Access-Control-Allow-Origin`, method not allowed, etc.).

2. Validate origin in request vs `CorsOptions:AllowedOrigins`:

```bash
# Test preflight manually
curl -X OPTIONS https://api.example.com/endpoint \
  -H "Origin: https://app.example.com" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type, Authorization" \
  -v 2>&1 | grep -i "access-control"
```

3. Check whether `AllowCredentials: true` is paired with explicit origins (wildcards are invalid with credentials).

4. Verify the middleware is in the pipeline (`WithCors()` called in `UseApiPipeline`).

### Mitigate

- Add the missing origin to `CorsOptions:AllowedOrigins` and redeploy.
- If urgent, temporarily set `AllowAllInDevelopment: true` in a staging environment to confirm CORS is the issue (never in production).

### Follow-up

- Add all legitimate frontend origins to production config.
- Set up monitoring for CORS preflight failures.

---

## Alert: incorrect client IP / user identity in logs and limits

**Severity:** P1 (Critical) — rate limiting, logging, and audit trails are unreliable.

### Diagnose

1. Confirm `UseApiPipelineForwardedHeaders()` or `WithForwardedHeaders()` executes first in the pipeline.

2. Validate trusted proxy/network settings:

```bash
# Check current config
cat appsettings.Production.json | jq '.ForwardedHeadersOptions'
```

3. Confirm ingress adds expected `X-Forwarded-For` and `X-Forwarded-Proto` headers:

```bash
# From inside the pod
curl -v https://api.example.com/health 2>&1 | grep -i "x-forwarded"
```

4. Check if `ClearDefaultProxies` is `true` (required for most cloud/container environments).

### Mitigate

- Correct trust config: set `ClearDefaultProxies: true` and add your ingress CIDR to `KnownNetworks`.
- Redeploy.

### Follow-up

- Add startup validation: `EnforceTrustedProxyConfigurationInProduction: true` prevents this class of misconfiguration from deploying silently.

---

## Alert: repeated `5xx` with ProblemDetails

**Severity:** P1 (Critical) — unhandled exceptions reaching clients.

### Diagnose

1. Confirm exception handler middleware is enabled (`WithExceptionHandler()` in pipeline).

2. Use correlation ID + trace ID from response headers to locate root cause:

```bash
# Extract correlation ID from a failed response
curl -s -D - https://api.example.com/failing-endpoint | grep -i x-correlation-id

# Search logs by correlation ID
kubectl logs deploy/api-service | grep "CORRELATION_ID_VALUE"
```

3. Verify `Cache-Control: no-store` is present on error responses (prevents caching errors).

4. Check for recent deployment or config changes tied to the failure pattern.

### Mitigate

- Roll back recent middleware/config changes if failure is tied to rollout.
- If exception is in business logic, add error handling at the controller/handler level.

### Follow-up

- Add specific exception-to-status-code mappings if needed.
- Monitor `apipipeline.exceptions.handled` counter for trends.

---

## Alert: deprecated API version still heavily used

**Severity:** P3 (Medium) — planned deprecation not being adopted by clients.

### Diagnose

1. Inspect traffic by versioned route:

```promql
sum by (http_route) (rate(http_server_request_duration_seconds_count{http_route=~"/api/v1.*"}[1h]))
```

2. Verify deprecation/sunset headers are emitted:

```bash
curl -s -D - https://api.example.com/api/v1/resource | grep -iE "deprecation|sunset|link"
```

3. Check that `IApiVersionReader` is registered (requires `ApiPipeline.NET.Versioning` or custom implementation).

### Mitigate

- Notify consumers with migration timeline.
- Delay hard cutover if business impact is high; avoid silent removal.
- Consider adding rate limit tightening on deprecated versions to encourage migration.

### Follow-up

- Track deprecation header emission rate via `apipipeline.deprecation.headers_added` metric.
- Update sunset dates as timeline evolves.

---

## Incident communication checklist

| Question | Example |
|---|---|
| What changed? | Config rollout changed `PermitLimit` from 200 to 20 |
| Which routes/clients are affected? | All `/api/v2/*` routes, 15% of users seeing 429s |
| What mitigation is active? | Reverted `PermitLimit` to 200, redeployed |
| What config values were altered? | `RateLimitingOptions.Policies[0].PermitLimit` |
| What is the rollback condition? | Revert ConfigMap to previous version, restart pods |
| Who is the incident owner? | @oncall-engineer |
