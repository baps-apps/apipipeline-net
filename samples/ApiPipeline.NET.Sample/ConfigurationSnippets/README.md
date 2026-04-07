# Configuration snippets

Valid JSON fragments you can merge into your own `appsettings.json` or environment-specific files. Files are copied next to the sample executable on build.

| File | Intent |
|------|--------|
| `minimal.json` | Turn off most ApiPipeline features (`Enabled: false`) for a bare API. |
| `development-open-cors.json` | Development-style CORS + HSTS off + permissive rate limit. |
| `production-k8s-ingress.json` | Trusted proxy CIDRs + strict CORS + `AnonymousFallback: Reject`. |
| `anonymous-fallback.json` | Example `RateLimitingOptions` when tuning null client IP behavior. |
| `prefer-output-caching.json` | Set `PreferOutputCaching` and adopt the Output Caching satellite (see main README). |
