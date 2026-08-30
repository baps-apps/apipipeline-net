# Troubleshooting

Cross-feature problems. Feature-specific ones are in each guide ([index](#by-feature)); for a firing
alert, use the [runbook](RUNBOOK.md).

## A feature does nothing

Check the startup log line `ApiPipeline.NET pipeline composed, in execution order: [...]`. A feature
runs only when its phase is listed **and** its `Enabled` was `true` at startup
([defaults](../README.md#features-and-defaults)). Either half alone is logged:

- **Event 15**, `WithCors() is in the pipeline, but ApiPipeline:Cors:Enabled is false`: enable it or remove the
  call (expected where the feature is off on purpose). For security phases, add
  `.RequireSecurityFeaturesEnabled()` to make this a startup failure outside Development
  ([README](../README.md#quick-start)).
- **Event 13**, `ApiPipeline:RateLimiting:Enabled is true, but its phase was never added`: add the `With*()`
  call (event 14: `UseApiPipeline` was never called). Middleware you add yourself isn't seen.

## The app fails to start

Startup fails on purpose for unsafe or ineffective settings; the message names the setting
(`OptionsValidationException` for options, `InvalidOperationException` for guards). Find the feature
from the key and check its guide. Messages common to all features:

| Message contains | Fix |
| --- | --- |
| `'...Seconds' is not read` | Rename to the duration key: `"Window": "00:01:00"` |
| `which reads as 60 days` | Write a duration, not a number |
| `must be between ... and ...` | Use a value in range |
| `is not a ... value. Use one of` | Use a listed name, not a number |
| `...Policies:i is named 'X' in ... and 'Y' in ...` | Lists merge by position: [restate the list](features/rate-limiting.md#changing-configuration) |
| `ExcludedPaths entry '...' is not a path` | Start entries with `/` |
| `pipeline order is unsafe` | Use the [recommended order](../README.md#pipeline-order) |
| `RequireSecurityFeaturesEnabled() is set, but` | Set the feature's `Enabled: true` for this environment, or remove its `With*()` call |
| `the feature's services are not registered` | Add the `AddApiPipeline*` call |
| `The configuration passed is the '...' section itself` | Pass the root or the `ApiPipeline` section |

## Restore fails with NU1605 for StackExchange.Redis

`Detected package downgrade: StackExchange.Redis from 3.4.35 to ...` means the application references
an older StackExchange.Redis while `ApiPipeline.NET.RateLimiting.Redis` needs 3.4.35 or later. Raise the
application's own reference (and its central `PackageVersion`, if any) to 3.4.35 or later. From 2.x that
is a major upgrade of the client: check its release notes, and test code that uses the client directly.

## A configuration edit did not take effect

Settings are read at startup: roll the deployment. If new pods fail to start, the old ones keep the
old values (`kubectl rollout status`). An empty list `[]` replaces a default list; omit the key to
keep the default.

## By feature

| Symptom | Guide |
| --- | --- |
| No limiting, too many 429s, Redis per pod | [rate-limiting.md](features/rate-limiting.md#troubleshooting) |
| Wrong client IP, redirect loop | [forwarded-headers.md](features/forwarded-headers.md#troubleshooting) |
| CORS failures | [cors.md](features/cors.md#troubleshooting) |
| Correlation ID missing or replaced | [correlation-id.md](features/correlation-id.md#troubleshooting) |
| Headers missing, Swagger UI blank | [security-headers.md](features/security-headers.md#troubleshooting) |
| Nothing compressed | [response-compression.md](features/response-compression.md#troubleshooting) |
| 413, slow uploads, `Server` header | [request-limits.md](features/request-limits.md#troubleshooting) |
| Timeouts | [request-timeouts.md](features/request-timeouts.md#troubleshooting) |
| Caching | [output-caching.md](features/output-caching.md#troubleshooting), [response-caching.md](features/response-caching.md#troubleshooting) |
| Deprecation headers missing | [version-deprecation.md](features/version-deprecation.md#troubleshooting) |
