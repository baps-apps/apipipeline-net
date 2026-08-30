# Configuration snippets

Fragments to merge into an existing `appsettings.json`; most aren't complete alone (a rate limiting
fragment without `Policies` fails startup).

| File | Contents |
| --- | --- |
| `minimal.json` | Features off |
| `development-open-cors.json` | Allow-all CORS, no HSTS, `permissive` default policy |
| `production-k8s-ingress.json` | Trusted ingress, strict CORS, `AnonymousFallback: Reject`, probe exemption narrowed to the node network, Kestrel limits, timeouts, HTTPS compression |
| `anonymous-fallback.json` | A complete rate limiting section with `AnonymousFallback: Reject` |
| `output-caching.json` | Output cache store settings; endpoints still need `.CacheOutput()` |
