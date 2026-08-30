# Compiler diagnostics

Build warnings raised by ApiPipeline.NET's API, each with its own ID so it can be suppressed alone:

```xml
<NoWarn>$(NoWarn);APIPIPELINE001</NoWarn>
```

## APIPIPELINE001

**Response caching is obsolete** and will be removed in the next major version. Use
[output caching](features/output-caching.md): it caches only what endpoints opt into, supports tags
and distributed stores.

1. Replace `WithResponseCaching()` with `WithOutputCaching()`.
2. Move `ApiPipeline:ResponseCaching` to `ApiPipeline:OutputCaching` (`Enabled`, `SizeLimitBytes`
   and `UseCaseSensitivePaths` carry over).
3. Add `.CacheOutput()` or `[OutputCache]` to each cacheable endpoint; output caching ignores
   response `Cache-Control`.
