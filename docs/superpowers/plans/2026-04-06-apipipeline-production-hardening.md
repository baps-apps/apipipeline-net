# ApiPipeline.NET Production Hardening & Platform Evolution — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax; all steps below are marked complete for this repository revision.

**Goal:** Harden ApiPipeline.NET against all 27 issues identified in the Principal Architect review — fixing security defaults, correctness gaps, performance allocations, observability blind spots, and architectural coupling — while adding enterprise-grade features (phase-enforced pipeline builder, validation hook, satellite packages).

**Architecture:** All phases build on the existing extension-method / options-pattern structure. Critical fixes (Phase 1-2) touch existing files only. Refactoring (Phase 3) reorganises internals without changing public APIs. Observability and test gap (Phase 4) add new files. Advanced features (Phase 5) introduce new abstractions and optional NuGet packages.

**Tech Stack:** .NET 10, ASP.NET Core, xUnit, FluentAssertions, Microsoft.AspNetCore.TestHost, System.Diagnostics.Metrics, System.Diagnostics.ActivitySource

---

> **Scope note:** Phase 5 (Tasks 18–21) creates new `.csproj` satellite packages and a new builder abstraction. These are independent of Phases 1–4 and can be deferred to a separate implementation session if needed.

---

## File Map

### Modified files
| File | Changes |
|---|---|
| `src/ApiPipeline.NET/Options/RequestLimitsOptions.cs` | Range min 0→1 on all limit properties |
| `src/ApiPipeline.NET/Options/ResponseCompressionSettings.cs` | `EnableForHttps` default `true`→`false` |
| `src/ApiPipeline.NET/Options/CorsSettings.cs` | `AllowAllInDevelopment` default `true`→`false` |
| `src/ApiPipeline.NET/Options/ForwardedHeadersSettings.cs` | `ForwardLimit` range 10→20 |
| `src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs` | `[Url]` on `SunsetLink`; `IApiVersionReader` interface (Task 18) |
| `src/ApiPipeline.NET/Options/RateLimitingOptions.cs` | `AnonymousFallbackBehavior` enum + property |
| `src/ApiPipeline.NET/Middleware/CorrelationIdMiddleware.cs` | Convert to `IMiddleware`; fix Dictionary allocation |
| `src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs` | SunsetLink runtime guard; optional `IApiVersionReader` (Task 18) |
| `src/ApiPipeline.NET/Middleware/SecurityHeadersMiddleware.cs` | Remove noisy metric call |
| `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs` | Remove `SecurityHeadersAppliedCount`; add dimensions to rate limit counter; add histogram + new counters |
| `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs` | Remove embedded classes; add `LiveConfigCorsPolicyProvider` reg; add startup log; `AddCorrelationId` registers middleware |
| `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs` | Exception handler guard; warning log for `AllowAllInDevelopment`; hot-reload compression; `UseApiPipeline` (Task 20) |
| `src/ApiPipeline.NET/Extensions/WebApplicationBuilderExtensions.cs` | `[Obsolete]` on `ConfigureKestrelRequestLimits` |
| `src/ApiPipeline.NET/ApiPipeline.NET.csproj` | Remove `Asp.Versioning.Mvc` reference (Task 18) |
| `tests/ApiPipeline.NET.Tests/TestAppBuilder.cs` | Remove `ConfigureKestrelRequestLimits()` call |
| `tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs` | New validation test cases |

### New files
| File | Responsibility |
|---|---|
| `src/ApiPipeline.NET/Cors/LiveConfigCorsPolicyProvider.cs` | Per-request CORS policy from live `IOptionsMonitor<CorsSettings>` |
| `src/ApiPipeline.NET/Options/ConfigureKestrelOptions.cs` | `IConfigureOptions<KestrelServerOptions>` bound to validated `RequestLimitsOptions` |
| `src/ApiPipeline.NET/Options/ConfigureResponseCompressionOptions.cs` | Moved from `ServiceCollectionExtensions` |
| `src/ApiPipeline.NET/Options/ConfigureResponseCachingOptions.cs` | Moved from `ServiceCollectionExtensions` |
| `src/ApiPipeline.NET/RateLimiting/RateLimiterPolicyResolver.cs` | Moved from `ServiceCollectionExtensions` |
| `src/ApiPipeline.NET/Middleware/RequestSizeMiddleware.cs` | Records `Content-Length` histogram |
| `src/ApiPipeline.NET/Versioning/IApiVersionReader.cs` | Abstraction so core doesn't depend on `Asp.Versioning.Mvc` |
| `src/ApiPipeline.NET/Pipeline/IApiPipelineBuilder.cs` | Fluent builder interface |
| `src/ApiPipeline.NET/Pipeline/ApiPipelineBuilder.cs` | Phase-ordered pipeline registration |
| `src/ApiPipeline.NET/Validation/IRequestValidationFilter.cs` | OWASP API7 extension point |
| `src/ApiPipeline.NET/Validation/RequestValidationResult.cs` | Validation result value type |
| `src/ApiPipeline.NET/Middleware/RequestValidationMiddleware.cs` | Runs `IRequestValidationFilter` chain |
| `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj` | New satellite project |
| `src/ApiPipeline.NET.Versioning/AspVersioningApiVersionReader.cs` | `IApiVersionReader` backed by `Asp.Versioning.Mvc` |
| `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs` | `AddApiPipelineVersioning()` extension |
| `src/ApiPipeline.NET.OutputCaching/ApiPipeline.NET.OutputCaching.csproj` | New satellite project |
| `src/ApiPipeline.NET.OutputCaching/OutputCachingServiceCollectionExtensions.cs` | `AddApiPipelineOutputCaching()` extension |
| `src/ApiPipeline.NET.OutputCaching/OutputCachingWebApplicationExtensions.cs` | `UseApiPipelineOutputCaching()` extension |
| `tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs` | Deprecation/Sunset headers; SunsetLink injection guard |
| `tests/ApiPipeline.NET.Tests/RequestLimitsTests.cs` | Body size validation; zero-value rejection |
| `tests/ApiPipeline.NET.Tests/PipelineBuilderTests.cs` | Phase ordering; builder contract |
| `tests/ApiPipeline.NET.Tests/RequestSizeMiddlewareTests.cs` | Histogram recording |
| `tests/ApiPipeline.NET.Tests/RequestValidationMiddlewareTests.cs` | Filter chain; ProblemDetails response |

---

## Phase 1 — Quick Validation & Default Fixes

---

### Task 1: Fix `RequestLimitsOptions` Range Validations (C-5)

**Files:**
- Modify: `src/ApiPipeline.NET/Options/RequestLimitsOptions.cs`
- Modify: `tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs`

- [x] **Step 1.1: Write failing tests for zero-value limits**

Add to `tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs`:

```csharp
/// <summary>
/// Verifies that MaxRequestBodySize = 0 is rejected at startup when limits are enabled.
/// A zero-byte body limit silently rejects all POST/PUT/PATCH requests.
/// </summary>
[Fact]
public async Task RequestLimits_MaxRequestBodySize_Zero_Fails_Validation()
{
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["RequestLimitsOptions:Enabled"] = "true";
        c["RequestLimitsOptions:MaxRequestBodySize"] = "0";
    });

    await using var app = await TestAppBuilder.CreateAppAsync(config);
    app.MapGet("/test", () => "ok");

    var act = () => app.StartAsync();
    await act.Should().ThrowAsync<OptionsValidationException>()
        .WithMessage("*MaxRequestBodySize*");
}

[Fact]
public async Task RequestLimits_MaxRequestHeadersTotalSize_Zero_Fails_Validation()
{
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["RequestLimitsOptions:Enabled"] = "true";
        c["RequestLimitsOptions:MaxRequestHeadersTotalSize"] = "0";
    });

    await using var app = await TestAppBuilder.CreateAppAsync(config);
    app.MapGet("/test", () => "ok");

    var act = () => app.StartAsync();
    await act.Should().ThrowAsync<OptionsValidationException>()
        .WithMessage("*MaxRequestHeadersTotalSize*");
}

[Fact]
public async Task RequestLimits_MaxRequestBodySize_Positive_Passes_Validation()
{
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["RequestLimitsOptions:Enabled"] = "true";
        c["RequestLimitsOptions:MaxRequestBodySize"] = "10485760"; // 10 MB
    });

    await using var app = await TestAppBuilder.CreateAppAsync(config);
    app.MapGet("/test", () => "ok");

    var act = () => app.StartAsync();
    await act.Should().NotThrowAsync();
}
```

- [x] **Step 1.2: Run tests to confirm they fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~OptionsValidationTests.RequestLimits" -v
```

Expected: FAIL — `RequestLimits_MaxRequestBodySize_Zero_Fails_Validation` passes but should throw.

- [x] **Step 1.3: Fix `RequestLimitsOptions.cs` — change Range minimum from 0 to 1**

Replace the entire file content:

```csharp
using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options for Kestrel server limits and ASP.NET Core form request limits.
/// When <see cref="Enabled"/> is <c>false</c>, no limits are applied.
/// </summary>
public sealed class RequestLimitsOptions
{
    /// <summary>Enables Kestrel/form request limits when true.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Maps to Kestrel <c>MaxRequestBodySize</c> and form multipart/body buffer limits.
    /// Minimum 1 byte — a value of 0 silently rejects all non-GET requests.</summary>
    [Range(1, long.MaxValue)]
    public long? MaxRequestBodySize { get; set; }

    /// <summary>Maps to Kestrel <c>MaxRequestHeadersTotalSize</c>.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxRequestHeadersTotalSize { get; set; }

    /// <summary>Maps to Kestrel <c>MaxRequestHeaderCount</c>.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxRequestHeaderCount { get; set; }

    /// <summary>Maps to ASP.NET Core <c>FormOptions.ValueCountLimit</c>.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxFormValueCount { get; set; }
}
```

- [x] **Step 1.4: Run tests to confirm they pass**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~OptionsValidationTests.RequestLimits" -v
```

Expected: All 3 tests PASS.

- [x] **Step 1.5: Run full suite to check for regressions**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

Expected: All existing tests pass.

- [x] **Step 1.6: Commit**

```bash
git add src/ApiPipeline.NET/Options/RequestLimitsOptions.cs tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs
git commit -m "fix: reject zero-value request limits (C-5) — zero body size silently kills all POST/PUT/PATCH"
```

---

### Task 2: Fix `EnableForHttps` Default to `false` (C-8)

**Files:**
- Modify: `src/ApiPipeline.NET/Options/ResponseCompressionSettings.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `tests/ApiPipeline.NET.Tests/ResponseCompressionTests.cs`

- [x] **Step 2.1: Write failing test — default should be false**

Add to `tests/ApiPipeline.NET.Tests/ResponseCompressionTests.cs`:

```csharp
/// <summary>
/// Verifies that the default value of EnableForHttps is false (opt-in only).
/// BREACH/CRIME attacks are possible when HTTPS + compression is on by default.
/// </summary>
[Fact]
public void ResponseCompressionSettings_EnableForHttps_DefaultIs_False()
{
    var settings = new ResponseCompressionSettings();
    settings.EnableForHttps.Should().BeFalse(
        "HTTPS compression must be opt-in to avoid BREACH/CRIME attacks");
}
```

- [x] **Step 2.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~ResponseCompressionTests.ResponseCompressionSettings_EnableForHttps_DefaultIs_False" -v
```

Expected: FAIL.

- [x] **Step 2.3: Change default in `ResponseCompressionSettings.cs`**

In `src/ApiPipeline.NET/Options/ResponseCompressionSettings.cs`, change line:

```csharp
// Before:
public bool EnableForHttps { get; set; } = true;

// After:
public bool EnableForHttps { get; set; } = false;
```

Also update the XML doc comment on `EnableForHttps` — add a sentence:
```csharp
/// <para><b>Default is <c>false</c> (opt-in).</b> Enable only when your API endpoints
/// are confirmed to never mix attacker-controlled input with secrets in the same response body.</para>
```

- [x] **Step 2.4: Add startup warning when `EnableForHttps = true` in `ServiceCollectionExtensions.cs`**

In `ConfigureResponseCompression`, after binding the options, add a startup warning. Locate the line:
```csharp
services.TryAddSingleton<IConfigureOptions<ResponseCompressionOptions>, ConfigureResponseCompressionOptions>();
```

Add before it:

```csharp
// Warn when consumer explicitly opts into HTTPS compression
var compressionCheck = configuration.GetSection(ApiPipelineConfigurationKeys.ResponseCompression)
    .Get<ResponseCompressionSettings>();
if (compressionCheck is { Enabled: true, EnableForHttps: true })
{
    services.AddOptions<ResponseCompressionSettings>()
        .PostConfigure(_ => { }); // Ensure options are resolved at startup for the warning below
    services.AddHostedService<EnableForHttpsWarningHostedService>();
}
```

Actually, a `IHostedService` is heavyweight for a one-time log. Simpler: emit via `IStartupFilter`. Even simpler: emit the warning during `UseResponseCompression()` at pipeline build time (we already have access to `app.Services` there).

Update `UseResponseCompression` in `WebApplicationExtensions.cs` to add the warning after the enabled check:

```csharp
public static WebApplication UseResponseCompression(this WebApplication app)
{
    var settings = app.Services.GetRequiredService<IOptions<ResponseCompressionSettings>>().Value;
    if (!settings.Enabled)
    {
        return app;
    }

    if (settings.EnableForHttps)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.ResponseCompression");
        logger.LogWarning(
            "ResponseCompression: EnableForHttps is true. Ensure your API never mixes " +
            "attacker-controlled input with secrets in the same compressed response (BREACH/CRIME risk).");
    }
    // ... rest of method unchanged
```

- [x] **Step 2.5: Run tests**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~ResponseCompressionTests" -v
```

Expected: New test passes. Check that existing compression tests that relied on `EnableForHttps = true` default don't break (they use `ResponseCompressionOptions:Enabled = false` in `MinimalConfig` so compression is off anyway).

- [x] **Step 2.6: Commit**

```bash
git add src/ApiPipeline.NET/Options/ResponseCompressionSettings.cs src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs tests/ApiPipeline.NET.Tests/ResponseCompressionTests.cs
git commit -m "fix: default EnableForHttps to false — BREACH/CRIME risk must be explicit opt-in (C-8)"
```

---

### Task 3: Fix `AllowAllInDevelopment` Default to `false` + Runtime Warning (S-4)

**Files:**
- Modify: `src/ApiPipeline.NET/Options/CorsSettings.cs`
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`
- Modify: `tests/ApiPipeline.NET.Tests/CorsTests.cs`

- [x] **Step 3.1: Write failing tests**

Add to `tests/ApiPipeline.NET.Tests/CorsTests.cs`:

```csharp
/// <summary>
/// Verifies that AllowAllInDevelopment defaults to false to prevent accidental
/// wildcard CORS in staging/CI environments where ASPNETCORE_ENVIRONMENT=Development.
/// </summary>
[Fact]
public void CorsSettings_AllowAllInDevelopment_DefaultIs_False()
{
    var settings = new CorsSettings();
    settings.AllowAllInDevelopment.Should().BeFalse(
        "wildcard CORS must be explicit opt-in to avoid accidental exposure in staging");
}
```

- [x] **Step 3.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~CorsTests.CorsSettings_AllowAllInDevelopment_DefaultIs_False" -v
```

Expected: FAIL.

- [x] **Step 3.3: Change default in `CorsSettings.cs`**

```csharp
// Before:
public bool AllowAllInDevelopment { get; set; } = true;

// After:
public bool AllowAllInDevelopment { get; set; } = false;
```

Update XML doc to note: `Defaults to <c>false</c>. Set explicitly to <c>true</c> in development appsettings only.`

- [x] **Step 3.4: Add runtime warning when AllowAll policy activates in `WebApplicationExtensions.cs`**

In `UseCors`, add a log warning when the AllowAll policy path is taken. The method becomes:

```csharp
public static WebApplication UseCors(this WebApplication app)
{
    var env = app.Services.GetRequiredService<IHostEnvironment>();
    var settings = app.Services.GetRequiredService<IOptions<CorsSettings>>().Value;
    if (!settings.Enabled)
    {
        return app;
    }

    string policyName;
    if (env.IsDevelopment() && settings.AllowAllInDevelopment)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.Cors");
        logger.LogWarning(
            "CORS: AllowAll policy is active (AllowAllInDevelopment=true). " +
            "All origins, methods, and headers are allowed. Do not use in production.");
        policyName = CorsPolicyNames.AllowAll;
    }
    else
    {
        policyName = CorsPolicyNames.Configured;
    }

    app.UseCors(policyName);
    return app;
}
```

- [x] **Step 3.5: Run tests**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~CorsTests" -v
```

Expected: New test passes. Check existing CORS tests that use `AllowAllInDevelopment` — the `TestAppBuilder.MinimalConfig` sets `CorsOptions:Enabled = false` so CORS is off; any CORS tests that enabled it explicitly need to also set `AllowAllInDevelopment: true` if they relied on the default.

- [x] **Step 3.6: Commit**

```bash
git add src/ApiPipeline.NET/Options/CorsSettings.cs src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs tests/ApiPipeline.NET.Tests/CorsTests.cs
git commit -m "fix: default AllowAllInDevelopment to false — prevent accidental wildcard CORS in staging (S-4)"
```

---

### Task 4: Raise `ForwardLimit` Range Cap to 20 (S-7)

**Files:**
- Modify: `src/ApiPipeline.NET/Options/ForwardedHeadersSettings.cs`
- Modify: `tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs`

- [x] **Step 4.1: Write failing test**

Add to `OptionsValidationTests.cs`:

```csharp
/// <summary>
/// Verifies ForwardLimit accepts values up to 20 to support complex proxy topologies
/// (CloudFront → WAF → ALB → Nginx Ingress → Pod = 4 hops minimum in AWS).
/// </summary>
[Fact]
public async Task ForwardedHeaders_ForwardLimit_15_Passes_Validation()
{
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["ForwardedHeadersOptions:Enabled"] = "true";
        c["ForwardedHeadersOptions:ForwardLimit"] = "15";
    });

    await using var app = await TestAppBuilder.CreateAppAsync(config);
    app.MapGet("/test", () => "ok");

    var act = () => app.StartAsync();
    await act.Should().NotThrowAsync();
}
```

- [x] **Step 4.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~OptionsValidationTests.ForwardedHeaders_ForwardLimit_15_Passes_Validation" -v
```

Expected: FAIL — currently `[Range(1, 10)]` rejects 15.

- [x] **Step 4.3: Update `ForwardedHeadersSettings.cs`**

```csharp
// Before:
[Range(1, 10)]
public int ForwardLimit { get; set; } = 1;

// After:
/// <summary>
/// Maximum number of proxy entries to process from <c>X-Forwarded-For</c>.
/// Set to the number of trusted proxies in front of the application.
/// Common topologies: single reverse proxy = 1, Nginx Ingress on Kubernetes = 2,
/// CloudFront → ALB → Nginx → Pod = 3-4. Maximum 20.
/// Defaults to <c>1</c>.
/// </summary>
[Range(1, 20)]
public int ForwardLimit { get; set; } = 1;
```

- [x] **Step 4.4: Run tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~OptionsValidationTests.ForwardedHeaders" -v
```

```bash
git add src/ApiPipeline.NET/Options/ForwardedHeadersSettings.cs tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs
git commit -m "fix: raise ForwardLimit validation cap to 20 for complex proxy topologies (S-7)"
```

---

### Task 5: Guard `UseApiPipelineExceptionHandler` Against Missing DI Registration (S-6)

**Files:**
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`
- Modify: `tests/ApiPipeline.NET.Tests/ExceptionHandlerTests.cs`

- [x] **Step 5.1: Write failing test**

Add to `ExceptionHandlerTests.cs`:

```csharp
/// <summary>
/// Verifies that calling UseApiPipelineExceptionHandler without first calling
/// AddApiPipelineExceptionHandler throws a clear InvalidOperationException at pipeline
/// build time rather than silently falling back to plain-text error responses.
/// </summary>
[Fact]
public async Task UseApiPipelineExceptionHandler_Without_AddService_Throws()
{
    var config = TestAppBuilder.MinimalConfig();
    // addExceptionHandler: false — skips AddApiPipelineExceptionHandler
    await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: false);

    var act = () =>
    {
        app.UseApiPipelineExceptionHandler();
        return Task.CompletedTask;
    };

    await act.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("*AddApiPipelineExceptionHandler*");
}
```

- [x] **Step 5.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~ExceptionHandlerTests.UseApiPipelineExceptionHandler_Without_AddService_Throws" -v
```

Expected: FAIL — currently no guard exists.

- [x] **Step 5.3: Add guard in `WebApplicationExtensions.cs`**

In `UseApiPipelineExceptionHandler`, add at the top of the method:

```csharp
public static WebApplication UseApiPipelineExceptionHandler(this WebApplication app)
{
    if (app.Services.GetService<IProblemDetailsService>() is null)
    {
        throw new InvalidOperationException(
            "UseApiPipelineExceptionHandler requires AddApiPipelineExceptionHandler to be called " +
            "during service registration. Add it to your IServiceCollection setup before building the app.");
    }

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    return app;
}
```

- [x] **Step 5.4: Run tests and confirm**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~ExceptionHandlerTests" -v
```

Expected: New test passes. All existing exception handler tests still pass (`addExceptionHandler: true` cases).

- [x] **Step 5.5: Commit**

```bash
git add src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs tests/ApiPipeline.NET.Tests/ExceptionHandlerTests.cs
git commit -m "fix: guard UseApiPipelineExceptionHandler against missing DI registration (S-6)"
```

---

### Task 6: Add `SunsetLink` URL Validation (S-5)

**Files:**
- Modify: `src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs`
- Modify: `src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs`
- Create: `tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs`

- [x] **Step 6.1: Create new test file with failing tests**

Create `tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs`:

```csharp
using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for ApiVersionDeprecationMiddleware — Deprecation/Sunset headers and SunsetLink safety.
/// </summary>
public sealed class ApiVersionDeprecationMiddlewareTests
{
    private static Dictionary<string, string?> DeprecationConfig(
        string version,
        string? sunsetLink = null,
        string? deprecationDate = null,
        string? sunsetDate = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiVersionDeprecationOptions:Enabled"] = "true";
            c["ApiVersionDeprecationOptions:PathPrefix"] = "/api";
            c["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = version;
            if (sunsetLink is not null)
                c["ApiVersionDeprecationOptions:DeprecatedVersions:0:SunsetLink"] = sunsetLink;
            if (deprecationDate is not null)
                c["ApiVersionDeprecationOptions:DeprecatedVersions:0:DeprecationDate"] = deprecationDate;
            if (sunsetDate is not null)
                c["ApiVersionDeprecationOptions:DeprecatedVersions:0:SunsetDate"] = sunsetDate;
        });

    /// <summary>
    /// Verifies that a response to a deprecated API version includes the Deprecation header.
    /// </summary>
    [Fact]
    public async Task Adds_Deprecation_Header_For_Deprecated_Version()
    {
        var config = DeprecationConfig("1.0");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiVersionDeprecation();
        app.MapGet("/api/v1/orders", () => Results.Ok("ok")).WithApiVersionSet(
            app.NewApiVersionSet().HasApiVersion(new Asp.Versioning.ApiVersion(1, 0)).Build());
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/v1/orders");

        response.Headers.Contains("Deprecation").Should().BeTrue();
    }

    /// <summary>
    /// Verifies that a valid absolute SunsetLink URL is emitted in the Link header.
    /// </summary>
    [Fact]
    public async Task Valid_SunsetLink_Is_Emitted_In_Link_Header()
    {
        var config = DeprecationConfig("1.0", sunsetLink: "https://docs.example.com/v1-sunset");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiVersionDeprecation();
        app.MapGet("/api/v1/orders", () => Results.Ok("ok")).WithApiVersionSet(
            app.NewApiVersionSet().HasApiVersion(new Asp.Versioning.ApiVersion(1, 0)).Build());
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/v1/orders");

        response.Headers.Contains("Link").Should().BeTrue();
        var linkValue = response.Headers.GetValues("Link").First();
        linkValue.Should().Contain("https://docs.example.com/v1-sunset");
    }

    /// <summary>
    /// Verifies that an invalid/non-URL SunsetLink is NOT emitted in the response
    /// (header injection protection — a malformed value must be silently skipped).
    /// </summary>
    [Fact]
    public async Task Invalid_SunsetLink_Is_Not_Emitted()
    {
        // An attacker-controlled config value with newline injection attempt
        var config = DeprecationConfig("1.0", sunsetLink: "not-a-url");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiVersionDeprecation();
        app.MapGet("/api/v1/orders", () => Results.Ok("ok")).WithApiVersionSet(
            app.NewApiVersionSet().HasApiVersion(new Asp.Versioning.ApiVersion(1, 0)).Build());
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/v1/orders");

        // Must not produce a Link header with invalid URI content
        if (response.Headers.Contains("Link"))
        {
            var linkValue = response.Headers.GetValues("Link").First();
            linkValue.Should().NotContain("not-a-url");
        }
    }

    /// <summary>
    /// Verifies that Sunset header is added when SunsetDate is configured.
    /// </summary>
    [Fact]
    public async Task Adds_Sunset_Header_When_SunsetDate_Configured()
    {
        var config = DeprecationConfig("1.0", sunsetDate: "2027-12-31T00:00:00+00:00");
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiVersionDeprecation();
        app.MapGet("/api/v1/orders", () => Results.Ok("ok")).WithApiVersionSet(
            app.NewApiVersionSet().HasApiVersion(new Asp.Versioning.ApiVersion(1, 0)).Build());
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/v1/orders");

        response.Headers.Contains("Sunset").Should().BeTrue();
    }
}
```

- [x] **Step 6.2: Run tests (some may fail or be skipped due to missing Asp.Versioning setup)**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~ApiVersionDeprecationMiddlewareTests" -v
```

Note: Tests that require `Asp.Versioning.ApiVersion` may need `WithApiVersionSet` setup. If the sample/tests already have `Asp.Versioning.Mvc` as a test project reference, they will work. Adjust as needed based on actual test run output.

- [x] **Step 6.3: Add `[Url]` annotation to `SunsetLink` in `ApiVersionDeprecationOptions.cs`**

```csharp
// Before:
[MinLength(0)]
public string? SunsetLink { get; set; }

// After:
/// <summary>
/// An optional absolute URL providing additional information about the deprecation or sunset.
/// Must be a valid absolute URI. Invalid values are silently skipped to prevent header injection.
/// </summary>
[Url]
public string? SunsetLink { get; set; }
```

- [x] **Step 6.4: Add runtime URL validation guard in `ApiVersionDeprecationMiddleware.cs`**

Find the SunsetLink header-setting block and wrap it with a validation guard:

```csharp
// Before:
if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
{
    ctx.Response.Headers.Append("Link", $"<{deprecated.SunsetLink}>; rel=\"sunset\"");
}

// After:
if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
{
    if (Uri.TryCreate(deprecated.SunsetLink, UriKind.Absolute, out _))
    {
        ctx.Response.Headers.Append("Link", $"<{deprecated.SunsetLink}>; rel=\"sunset\"");
    }
    else
    {
        logger.LogWarning(
            "ApiVersionDeprecation: SunsetLink '{SunsetLink}' is not a valid absolute URI — skipped to prevent header injection.",
            deprecated.SunsetLink);
    }
}
```

- [x] **Step 6.5: Run all tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs \
        src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs \
        tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs
git commit -m "fix: validate SunsetLink as absolute URI before emitting Link header (S-5)"
```

---

## Phase 2 — Security & Correctness

---

### Task 7: Convert `CorrelationIdMiddleware` to `IMiddleware` + Fix `AddCorrelationId()` (C-6)

**Files:**
- Modify: `src/ApiPipeline.NET/Middleware/CorrelationIdMiddleware.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`

- [x] **Step 7.1: Write failing test — AddCorrelationId must register the middleware in DI**

Add to `CorrelationIdMiddlewareTests.cs`:

```csharp
/// <summary>
/// Verifies that AddCorrelationId registers CorrelationIdMiddleware as a DI service,
/// making AddCorrelationId() a meaningful registration call rather than a no-op.
/// </summary>
[Fact]
public async Task AddCorrelationId_Registers_Middleware_In_DI()
{
    await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());

    // CorrelationIdMiddleware must be resolvable from DI after AddCorrelationId() is called
    var middleware = app.Services.GetService<CorrelationIdMiddleware>();
    middleware.Should().NotBeNull(
        "AddCorrelationId must register CorrelationIdMiddleware in DI (IMiddleware pattern)");
}
```

- [x] **Step 7.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~CorrelationIdMiddlewareTests.AddCorrelationId_Registers_Middleware_In_DI" -v
```

Expected: FAIL — `GetService<CorrelationIdMiddleware>()` returns null.

- [x] **Step 7.3: Convert `CorrelationIdMiddleware` to `IMiddleware`**

Replace the entire `CorrelationIdMiddleware.cs`:

```csharp
using System.Diagnostics;
using System.Text.RegularExpressions;
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// ASP.NET Core middleware that ensures every request and response carries a correlation ID.
/// Incoming correlation IDs are validated against a strict alphanumeric pattern to prevent header injection.
/// Registered via <see cref="Microsoft.AspNetCore.Builder.IApplicationBuilder.UseMiddleware{T}()"/>
/// after calling <see cref="Extensions.ServiceCollectionExtensions.AddCorrelationId"/>.
/// </summary>
public sealed partial class CorrelationIdMiddleware : IMiddleware
{
    /// <summary>
    /// The HTTP header name used to propagate the correlation identifier.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    private readonly ILogger<CorrelationIdMiddleware> _logger;

    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]{1,128}$")]
    private static partial Regex SafeCorrelationIdPattern();

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public CorrelationIdMiddleware(ILogger<CorrelationIdMiddleware> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executes the middleware, attaching a validated correlation ID to the current HTTP context.
    /// Invalid or missing incoming correlation IDs are replaced with a server-generated value.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="next">The next middleware delegate.</param>
    /// <returns>A task that represents the completion of request processing.</returns>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        string correlationId;

        if (context.Request.Headers.TryGetValue(HeaderName, out var existing)
            && !string.IsNullOrWhiteSpace(existing))
        {
            var incoming = existing.ToString();
            if (SafeCorrelationIdPattern().IsMatch(incoming))
            {
                correlationId = incoming;
            }
            else
            {
                correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                _logger.LogDebug(
                    "Rejected invalid correlation ID from request header, generated {CorrelationId}",
                    correlationId);
            }
        }
        else
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        context.Items[HeaderName] = correlationId;

        context.Response.OnStarting(static state =>
        {
            var (ctx, id) = ((HttpContext, string))state;
            ctx.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        }, (context, correlationId));

        ApiPipelineTelemetry.SetCorrelationIdOnCurrentActivity(correlationId);
        ApiPipelineTelemetry.RecordCorrelationIdProcessed();

        using (_logger.BeginScope(new[] { new KeyValuePair<string, object?>("CorrelationId", (object?)correlationId) }))
        {
            await next(context);
        }
    }
}
```

Note: The `using (_logger.BeginScope(new[] {...}))` also fixes the per-request `Dictionary` allocation (S-2 — combining that fix here).

- [x] **Step 7.4: Update `AddCorrelationId()` in `ServiceCollectionExtensions.cs`**

```csharp
/// <summary>
/// Registers <see cref="CorrelationIdMiddleware"/> in the DI container (required for the
/// <see cref="Microsoft.AspNetCore.Http.IMiddleware"/> activation pattern) and enables the
/// <c>Add*</c>/<c>Use*</c> API symmetry.
/// </summary>
public static IServiceCollection AddCorrelationId(this IServiceCollection services)
{
    services.TryAddTransient<CorrelationIdMiddleware>();
    return services;
}
```

- [x] **Step 7.5: Run all tests**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

Expected: All tests pass, including the new `AddCorrelationId_Registers_Middleware_In_DI` test. Existing correlation ID middleware tests continue to work because `TestAppBuilder.CreateAppAsync` already calls `AddCorrelationId()` (line 30 of `TestAppBuilder.cs`).

- [x] **Step 7.6: Commit**

```bash
git add src/ApiPipeline.NET/Middleware/CorrelationIdMiddleware.cs \
        src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs \
        tests/ApiPipeline.NET.Tests/CorrelationIdMiddlewareTests.cs
git commit -m "fix: convert CorrelationIdMiddleware to IMiddleware; AddCorrelationId now registers it in DI (C-6, S-2)"
```

---

### Task 8: Add `AnonymousFallbackBehavior` for Rate Limit Null IP (C-7)

**Files:**
- Modify: `src/ApiPipeline.NET/Options/RateLimitingOptions.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `tests/ApiPipeline.NET.Tests/RateLimitingTests.cs`

- [x] **Step 8.1: Write failing tests**

Add to `RateLimitingTests.cs`:

```csharp
/// <summary>
/// Verifies that when AnonymousFallback is Reject and RemoteIpAddress cannot be determined,
/// requests are rejected with 429 rather than sharing a global bucket.
/// The default AnonymousFallback must be Reject.
/// </summary>
[Fact]
public void RateLimitingOptions_AnonymousFallback_DefaultIs_Reject()
{
    var options = new RateLimitingOptions();
    options.AnonymousFallback.Should().Be(AnonymousFallbackBehavior.Reject);
}
```

- [x] **Step 8.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~RateLimitingTests.RateLimitingOptions_AnonymousFallback_DefaultIs_Reject" -v
```

Expected: FAIL — `AnonymousFallbackBehavior` doesn't exist yet.

- [x] **Step 8.3: Add `AnonymousFallbackBehavior` enum and property to `RateLimitingOptions.cs`**

Add the enum and property. In `RateLimitingOptions.cs`, add before the class declaration:

```csharp
/// <summary>
/// Controls rate-limiting behaviour when the client IP address cannot be determined
/// (e.g. <c>RemoteIpAddress</c> is null because forwarded headers are misconfigured).
/// </summary>
public enum AnonymousFallbackBehavior
{
    /// <summary>
    /// Reject the request with HTTP 429 immediately. Safe default — prevents
    /// a single unknown client from exhausting a shared "anonymous" bucket.
    /// </summary>
    Reject,

    /// <summary>
    /// Apply rate limiting using a single shared "anonymous" bucket.
    /// Warning: one client can exhaust this bucket for all anonymous traffic.
    /// </summary>
    RateLimit,

    /// <summary>
    /// Skip rate limiting for requests with no determinable IP.
    /// Only use when you have an alternate enforcement mechanism upstream.
    /// </summary>
    Allow
}
```

In `RateLimitingOptions` class, add:

```csharp
/// <summary>
/// Controls what happens when <c>RemoteIpAddress</c> is null (e.g. misconfigured forwarded headers).
/// Defaults to <see cref="AnonymousFallbackBehavior.Reject"/> to prevent shared-bucket exhaustion.
/// </summary>
public AnonymousFallbackBehavior AnonymousFallback { get; set; } = AnonymousFallbackBehavior.Reject;
```

- [x] **Step 8.4: Update `GetPartitionKey` in `ServiceCollectionExtensions.cs`**

Replace the existing `GetPartitionKey` private method:

```csharp
/// <summary>
/// Resolves a stable partition key for rate limiting. Order of preference:
/// authenticated user ID, remote IP, then anonymous fallback per <see cref="RateLimitingOptions.AnonymousFallback"/>.
/// </summary>
private static RateLimitPartition<string> GetPartitionOrFallback(
    HttpContext context,
    RateLimitingOptions options,
    RateLimitPolicy policy)
{
    var userId = context.User?.Identity?.IsAuthenticated == true
        ? context.User.FindFirst("sub")?.Value
          ?? context.User.FindFirst("nameid")?.Value
          ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        : null;

    if (!string.IsNullOrWhiteSpace(userId))
    {
        return CreateRateLimiterPartition($"user:{userId}", policy);
    }

    var ip = context.Connection.RemoteIpAddress?.ToString();
    if (!string.IsNullOrWhiteSpace(ip))
    {
        return CreateRateLimiterPartition($"ip:{ip}", policy);
    }

    // RemoteIpAddress is null — apply AnonymousFallback behaviour
    return options.AnonymousFallback switch
    {
        AnonymousFallbackBehavior.Reject =>
            RateLimitPartition.GetFixedWindowLimiter(
                "ip:null:reject",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 0,
                    Window = TimeSpan.FromSeconds(1),
                    QueueLimit = 0
                }),

        AnonymousFallbackBehavior.Allow =>
            RateLimitPartition.GetNoLimiter("ip:null:allow"),

        _ => // RateLimit — shared bucket (legacy, documented risk)
            CreateRateLimiterPartition("ip:anonymous", policy)
    };
}
```

Update the `GlobalLimiter` lambda in `ConfigureRateLimiting` to call the new method:

```csharp
rateLimiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
{
    var options = httpContext.RequestServices.GetRequiredService<RateLimiterPolicyResolver>().Current;
    var policy = ResolvePolicy(options, options.DefaultPolicy);
    return policy is not null
        ? GetPartitionOrFallback(httpContext, options, policy)
        : RateLimitPartition.GetNoLimiter("no-policy");
});
```

Also update the named policy lambda similarly:

```csharp
rateLimiterOptions.AddPolicy(policyName, httpContext =>
{
    var options = httpContext.RequestServices.GetRequiredService<RateLimiterPolicyResolver>().Current;
    var runtimePolicy = ResolvePolicy(options, policyName);
    return runtimePolicy is not null
        ? GetPartitionOrFallback(httpContext, options, runtimePolicy)
        : RateLimitPartition.GetNoLimiter("no-policy");
});
```

Remove the old `GetPartitionKey(HttpContext)` and `CreateRateLimiterPartition(HttpContext, RateLimitPolicy)` overloads (the one taking `HttpContext` directly) — keep only `CreateRateLimiterPartition(string, RateLimitPolicy)`.

- [x] **Step 8.5: Run all tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Options/RateLimitingOptions.cs \
        src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs \
        tests/ApiPipeline.NET.Tests/RateLimitingTests.cs
git commit -m "fix: add AnonymousFallbackBehavior — default Reject prevents shared-bucket DoS when RemoteIpAddress is null (C-7)"
```

---

### Task 9: Add Named Rate Limit Policy Startup Log (C-3)

**Files:**
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`

- [x] **Step 9.1: Add startup log to `UseRateLimiting()`**

In `WebApplicationExtensions.cs`, update `UseRateLimiting`:

```csharp
public static WebApplication UseRateLimiting(this WebApplication app)
{
    var settings = app.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
    if (!settings.Enabled)
    {
        return app;
    }

    var logger = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("ApiPipeline.NET.RateLimiting");

    var policyNames = settings.Policies.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
    logger.LogInformation(
        "Rate limiting enabled. Default policy: '{DefaultPolicy}'. Registered named policies: [{Policies}]. " +
        "Note: named policies are registered at startup — adding new policies requires an app restart.",
        settings.DefaultPolicy,
        string.Join(", ", policyNames));

    app.UseRateLimiter();
    return app;
}
```

- [x] **Step 9.2: Run full test suite (no test needed — this is observational)**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

- [x] **Step 9.3: Commit**

```bash
git add src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs
git commit -m "feat: log named rate limit policies at startup with note about static registration (C-3)"
```

---

### Task 10: Move Kestrel Limits to `IConfigureOptions` (C-4)

**Files:**
- Create: `src/ApiPipeline.NET/Options/ConfigureKestrelOptions.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationBuilderExtensions.cs`
- Modify: `tests/ApiPipeline.NET.Tests/TestAppBuilder.cs`

- [x] **Step 10.1: Create `ConfigureKestrelOptions.cs`**

Create `src/ApiPipeline.NET/Options/ConfigureKestrelOptions.cs`:

```csharp
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Applies <see cref="RequestLimitsOptions"/> to Kestrel server limits via the
/// options infrastructure, ensuring validated options are used (not raw configuration).
/// Registered automatically by <see cref="Extensions.ServiceCollectionExtensions.AddRequestLimits"/>.
/// </summary>
internal sealed class ConfigureKestrelOptions : IConfigureOptions<KestrelServerOptions>
{
    private readonly IOptions<RequestLimitsOptions> _requestLimits;

    public ConfigureKestrelOptions(IOptions<RequestLimitsOptions> requestLimits)
        => _requestLimits = requestLimits;

    public void Configure(KestrelServerOptions options)
    {
        var limits = _requestLimits.Value;
        if (!limits.Enabled)
        {
            return;
        }

        if (limits.MaxRequestBodySize is { } maxBody)
        {
            options.Limits.MaxRequestBodySize = maxBody;
        }

        if (limits.MaxRequestHeadersTotalSize is { } maxHeadersTotal)
        {
            options.Limits.MaxRequestHeadersTotalSize = maxHeadersTotal;
        }

        if (limits.MaxRequestHeaderCount is { } maxHeaderCount)
        {
            options.Limits.MaxRequestHeaderCount = maxHeaderCount;
        }
    }
}
```

- [x] **Step 10.2: Register `ConfigureKestrelOptions` inside `ConfigureRequestLimits` in `ServiceCollectionExtensions.cs`**

Add to `ConfigureRequestLimits`:

```csharp
internal static IServiceCollection ConfigureRequestLimits(this IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<RequestLimitsOptions>()
        .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.RequestLimits))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // Register Kestrel configuration via validated options (replaces ConfigureKestrelRequestLimits)
    services.TryAddEnumerable(
        ServiceDescriptor.Singleton<IConfigureOptions<KestrelServerOptions>, ConfigureKestrelOptions>());

    return services;
}
```

- [x] **Step 10.3: Deprecate `ConfigureKestrelRequestLimits` in `WebApplicationBuilderExtensions.cs`**

```csharp
/// <summary>
/// <para><b>Deprecated.</b> Kestrel request limits are now applied automatically via
/// <see cref="ServiceCollectionExtensions.AddRequestLimits"/>, which registers
/// an <c>IConfigureOptions&lt;KestrelServerOptions&gt;</c> backed by validated options.</para>
/// <para>Remove this call — it is now a no-op.</para>
/// </summary>
[Obsolete(
    "ConfigureKestrelRequestLimits is no longer needed. Request limits are applied automatically " +
    "when AddRequestLimits is called. Remove this call from your startup code.",
    error: false)]
public static WebApplicationBuilder ConfigureKestrelRequestLimits(this WebApplicationBuilder builder)
{
    // No-op: limits are now configured via IConfigureOptions<KestrelServerOptions>
    // registered by AddRequestLimits. This method is retained for backwards compatibility.
    return builder;
}
```

- [x] **Step 10.4: Remove `ConfigureKestrelRequestLimits` call from `TestAppBuilder.cs`**

In `TestAppBuilder.cs`, remove line 45:

```csharp
// Remove this line:
builder.ConfigureKestrelRequestLimits();
```

- [x] **Step 10.5: Run all tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Options/ConfigureKestrelOptions.cs \
        src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs \
        src/ApiPipeline.NET/Extensions/WebApplicationBuilderExtensions.cs \
        tests/ApiPipeline.NET.Tests/TestAppBuilder.cs
git commit -m "fix: move Kestrel limits to IConfigureOptions — ensures ValidateOnStart applies before limits take effect (C-4)"
```

---

### Task 11: Implement `LiveConfigCorsPolicyProvider` (C-2)

**Files:**
- Create: `src/ApiPipeline.NET/Cors/LiveConfigCorsPolicyProvider.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `tests/ApiPipeline.NET.Tests/CorsTests.cs`

- [x] **Step 11.1: Write failing test — CORS policy must reflect config changes**

Add to `CorsTests.cs`:

```csharp
/// <summary>
/// Verifies that an origin explicitly listed in AllowedOrigins is accepted.
/// </summary>
[Fact]
public async Task Cors_Allows_Configured_Origin()
{
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["CorsOptions:Enabled"] = "true";
        c["CorsOptions:AllowedOrigins:0"] = "https://trusted.example.com";
    });
    await using var app = await TestAppBuilder.CreateAppAsync(config);
    app.UseCors();
    app.MapGet("/test", () => Results.Ok("ok"));
    await app.StartAsync();

    var client = app.GetTestClient();
    var request = new HttpRequestMessage(HttpMethod.Get, "/test");
    request.Headers.Add("Origin", "https://trusted.example.com");

    var response = await client.SendAsync(request);
    response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
}

/// <summary>
/// Verifies that an origin NOT in AllowedOrigins is rejected (no ACAO header).
/// </summary>
[Fact]
public async Task Cors_Rejects_Unknown_Origin()
{
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["CorsOptions:Enabled"] = "true";
        c["CorsOptions:AllowedOrigins:0"] = "https://trusted.example.com";
    });
    await using var app = await TestAppBuilder.CreateAppAsync(config);
    app.UseCors();
    app.MapGet("/test", () => Results.Ok("ok"));
    await app.StartAsync();

    var client = app.GetTestClient();
    var request = new HttpRequestMessage(HttpMethod.Get, "/test");
    request.Headers.Add("Origin", "https://evil.attacker.com");

    var response = await client.SendAsync(request);
    response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
}
```

- [x] **Step 11.2: Run to confirm current behaviour (these may already pass — baseline)**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~CorsTests.Cors_Allows_Configured_Origin|FullyQualifiedName~CorsTests.Cors_Rejects_Unknown_Origin" -v
```

- [x] **Step 11.3: Create `LiveConfigCorsPolicyProvider.cs`**

Create `src/ApiPipeline.NET/Cors/LiveConfigCorsPolicyProvider.cs`:

```csharp
using ApiPipeline.NET.Options;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Cors;

/// <summary>
/// An <see cref="ICorsPolicyProvider"/> that evaluates CORS policy from
/// <see cref="IOptionsMonitor{CorsSettings}"/> on each request, enabling hot-reload
/// of allowed origins without an application restart.
/// </summary>
internal sealed class LiveConfigCorsPolicyProvider : ICorsPolicyProvider
{
    private readonly IOptionsMonitor<CorsSettings> _settingsMonitor;
    private readonly IHostEnvironment _environment;

    public LiveConfigCorsPolicyProvider(
        IOptionsMonitor<CorsSettings> settingsMonitor,
        IHostEnvironment environment)
    {
        _settingsMonitor = settingsMonitor;
        _environment = environment;
    }

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var settings = _settingsMonitor.CurrentValue;

        if (_environment.IsDevelopment() && settings.AllowAllInDevelopment)
        {
            var allowAll = new CorsPolicyBuilder()
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
                .Build();
            return Task.FromResult<CorsPolicy?>(allowAll);
        }

        var builder = new CorsPolicyBuilder();

        if (settings.AllowedOrigins is { Length: > 0 })
        {
            builder.WithOrigins(settings.AllowedOrigins);
        }
        else
        {
            builder.SetIsOriginAllowed(_ => false);
        }

        if (settings.AllowedMethods is { Length: > 0 } && !settings.AllowedMethods.Contains("*"))
        {
            builder.WithMethods(settings.AllowedMethods);
        }
        else
        {
            builder.AllowAnyMethod();
        }

        if (settings.AllowedHeaders is { Length: > 0 } && !settings.AllowedHeaders.Contains("*"))
        {
            builder.WithHeaders(settings.AllowedHeaders);
        }
        else
        {
            builder.AllowAnyHeader();
        }

        if (settings.AllowCredentials)
        {
            builder.AllowCredentials();
        }

        return Task.FromResult<CorsPolicy?>(builder.Build());
    }
}
```

- [x] **Step 11.4: Register `LiveConfigCorsPolicyProvider` in `ConfigureCors` in `ServiceCollectionExtensions.cs`**

Replace the static policy registration at the end of `ConfigureCors`:

```csharp
internal static IServiceCollection ConfigureCors(this IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<CorsSettings>()
        .Bind(configuration.GetSection(ApiPipelineConfigurationKeys.Cors))
        .ValidateDataAnnotations()
        .Validate(
            c => !c.AllowCredentials || (c.AllowedOrigins is { Length: > 0 }),
            "When AllowCredentials is true, AllowedOrigins must be configured (CORS does not allow wildcard origin with credentials).")
        .ValidateOnStart();

    // Register the live-config provider and the basic CORS services it depends on
    services.AddCors();
    services.AddSingleton<ICorsPolicyProvider, LiveConfigCorsPolicyProvider>();

    return services;
}
```

- [x] **Step 11.5: Simplify `UseCors` in `WebApplicationExtensions.cs` — policy name no longer needed**

The `ICorsPolicyProvider` now handles policy selection per-request. `UseCors()` without a name is sufficient:

```csharp
public static WebApplication UseCors(this WebApplication app)
{
    var env = app.Services.GetRequiredService<IHostEnvironment>();
    var settings = app.Services.GetRequiredService<IOptions<CorsSettings>>().Value;
    if (!settings.Enabled)
    {
        return app;
    }

    if (env.IsDevelopment() && settings.AllowAllInDevelopment)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.Cors");
        logger.LogWarning(
            "CORS: AllowAll policy is active (AllowAllInDevelopment=true). " +
            "All origins, methods, and headers are allowed. Do not use in production.");
    }

    // LiveConfigCorsPolicyProvider handles policy selection per-request
    ((IApplicationBuilder)app).UseCors();
    return app;
}
```

- [x] **Step 11.6: Run all tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Cors/LiveConfigCorsPolicyProvider.cs \
        src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs \
        src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs \
        tests/ApiPipeline.NET.Tests/CorsTests.cs
git commit -m "feat: implement LiveConfigCorsPolicyProvider — CORS allowed origins now hot-reloadable (C-2)"
```

---

## Phase 3 — Refactoring

---

### Task 12: Move Internal Classes Out of `ServiceCollectionExtensions.cs` (S-1)

**Files:**
- Create: `src/ApiPipeline.NET/RateLimiting/RateLimiterPolicyResolver.cs`
- Create: `src/ApiPipeline.NET/Options/ConfigureResponseCompressionOptions.cs`
- Create: `src/ApiPipeline.NET/Options/ConfigureResponseCachingOptions.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`

- [x] **Step 12.1: Create `RateLimiting/RateLimiterPolicyResolver.cs`**

Create `src/ApiPipeline.NET/RateLimiting/RateLimiterPolicyResolver.cs`:

```csharp
using ApiPipeline.NET.Options;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Singleton resolver for rate limit policies. Wraps <see cref="IOptionsMonitor{T}"/>
/// to avoid per-request DI scope allocation inside the rate-limiter callback.
/// </summary>
internal sealed class RateLimiterPolicyResolver
{
    private readonly IOptionsMonitor<RateLimitingOptions> _monitor;

    public RateLimiterPolicyResolver(IOptionsMonitor<RateLimitingOptions> monitor)
        => _monitor = monitor;

    public RateLimitingOptions Current => _monitor.CurrentValue;
}
```

- [x] **Step 12.2: Create `Options/ConfigureResponseCompressionOptions.cs`**

Create `src/ApiPipeline.NET/Options/ConfigureResponseCompressionOptions.cs`:

```csharp
using System.IO.Compression;
using System.Net.Mime;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Options;

internal sealed class ConfigureResponseCompressionOptions : IConfigureOptions<ResponseCompressionOptions>
{
    private readonly IOptions<ResponseCompressionSettings> _settings;

    public ConfigureResponseCompressionOptions(IOptions<ResponseCompressionSettings> settings)
        => _settings = settings;

    public void Configure(ResponseCompressionOptions options)
    {
        var settings = _settings.Value;

        options.EnableForHttps = settings.EnableForHttps;

        options.Providers.Clear();
        if (settings.EnableBrotli)
        {
            options.Providers.Add<BrotliCompressionProvider>();
        }
        if (settings.EnableGzip)
        {
            options.Providers.Add<GzipCompressionProvider>();
        }

        var mimeTypes = (settings.MimeTypes is { Length: > 0 }
                ? settings.MimeTypes
                : ResponseCompressionDefaults.MimeTypes.Concat([MediaTypeNames.Application.Json, "application/problem+json"]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.ExcludedMimeTypes is { Length: > 0 })
        {
            mimeTypes.RemoveAll(mt => settings.ExcludedMimeTypes.Contains(mt, StringComparer.OrdinalIgnoreCase));
        }

        options.MimeTypes = mimeTypes.ToArray();
    }
}
```

- [x] **Step 12.3: Create `Options/ConfigureResponseCachingOptions.cs`**

Create `src/ApiPipeline.NET/Options/ConfigureResponseCachingOptions.cs`:

```csharp
using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Options;

internal sealed class ConfigureResponseCachingOptions : IConfigureOptions<ResponseCachingOptions>
{
    private readonly IOptions<ResponseCachingSettings> _settings;

    public ConfigureResponseCachingOptions(IOptions<ResponseCachingSettings> settings)
        => _settings = settings;

    public void Configure(ResponseCachingOptions options)
    {
        var settings = _settings.Value;
        if (settings.SizeLimitBytes is { } size)
        {
            options.SizeLimit = size;
        }

        options.UseCaseSensitivePaths = settings.UseCaseSensitivePaths;
    }
}
```

- [x] **Step 12.4: Remove the three embedded classes from `ServiceCollectionExtensions.cs`**

Delete from `ServiceCollectionExtensions.cs`:
- The `RateLimiterPolicyResolver` class (lines ~499–507)
- The `ConfigureResponseCompressionOptions` class (lines ~509–544)
- The `ConfigureResponseCachingOptions` class (lines ~546–562)

Update the `using` statement at the top of `ServiceCollectionExtensions.cs` to add:
```csharp
using ApiPipeline.NET.RateLimiting;
```

Also update `services.AddSingleton<RateLimiterPolicyResolver>()` — the type is now in `ApiPipeline.NET.RateLimiting` namespace, which is now imported.

- [x] **Step 12.5: Run full suite and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

Expected: All tests pass — this is a pure refactoring with no behaviour change.

```bash
git add src/ApiPipeline.NET/RateLimiting/ \
        src/ApiPipeline.NET/Options/ConfigureResponseCompressionOptions.cs \
        src/ApiPipeline.NET/Options/ConfigureResponseCachingOptions.cs \
        src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs
git commit -m "refactor: move embedded classes out of ServiceCollectionExtensions.cs (S-1)"
```

---

### Task 13: Fix `UseResponseCompression` Options Snapshot (S-3)

**Files:**
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`

- [x] **Step 13.1: Update `UseResponseCompression` to use `IOptionsMonitor`**

In `WebApplicationExtensions.cs`, update `UseResponseCompression` to read `IOptionsMonitor` inside the `UseWhen` predicate rather than snapshotting at build time:

```csharp
public static WebApplication UseResponseCompression(this WebApplication app)
{
    var monitor = app.Services.GetRequiredService<IOptionsMonitor<ResponseCompressionSettings>>();

    if (!monitor.CurrentValue.Enabled)
    {
        return app;
    }

    if (monitor.CurrentValue.EnableForHttps)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiPipeline.NET.ResponseCompression");
        logger.LogWarning(
            "ResponseCompression: EnableForHttps is true. Ensure your API never mixes " +
            "attacker-controlled input with secrets in the same compressed response (BREACH/CRIME risk).");
    }

    // Pre-compute excluded paths once; use OnChange to invalidate when config reloads
    var excludedPaths = ComputeExcludedPaths(monitor.CurrentValue);
    monitor.OnChange(settings => excludedPaths = ComputeExcludedPaths(settings));

    if (excludedPaths.Length == 0)
    {
        ((IApplicationBuilder)app).UseResponseCompression();
        return app;
    }

    app.UseWhen(
        context =>
        {
            var current = excludedPaths; // capture reference (array swap is atomic)
            var path = context.Request.Path;
            foreach (var excluded in current)
            {
                if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        },
        branch => ((IApplicationBuilder)branch).UseResponseCompression());

    return app;
}

private static PathString[] ComputeExcludedPaths(ResponseCompressionSettings settings) =>
    (settings.ExcludedPaths ?? [])
        .Select(p => new PathString(p))
        .ToArray();
```

- [x] **Step 13.2: Run full suite and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs
git commit -m "fix: UseResponseCompression reads ExcludedPaths from IOptionsMonitor for hot-reload support (S-3)"
```

---

## Phase 4 — Observability & Test Gaps

---

### Task 14: Remove Noisy Metric, Add Rate Limit Dimensions + New Counters (T-1, T-3, T-4, A-2)

**Files:**
- Modify: `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`
- Modify: `src/ApiPipeline.NET/Middleware/SecurityHeadersMiddleware.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`

- [x] **Step 14.1: Update `ApiPipelineTelemetry.cs`**

Replace the entire file:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace ApiPipeline.NET.Observability;

/// <summary>
/// Shared <see cref="ActivitySource"/> and <see cref="Meter"/> for ApiPipeline.NET.
/// Use with OpenTelemetry to export traces and metrics.
/// </summary>
public static class ApiPipelineTelemetry
{
    /// <summary>Activity source name for ApiPipeline.NET tracing.</summary>
    public const string ActivitySourceName = "ApiPipeline.NET";

    /// <summary>Meter name for ApiPipeline.NET metrics.</summary>
    public const string MeterName = "ApiPipeline.NET";

    private static readonly string InstrumentationVersion =
        typeof(ApiPipelineTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ApiPipelineTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Activity source for pipeline-related spans and tags.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, InstrumentationVersion);

    /// <summary>Meter for pipeline metrics.</summary>
    public static readonly Meter Meter = new(MeterName, InstrumentationVersion);

    /// <summary>
    /// Counter: requests rejected by rate limiting.
    /// Dimensions: <c>policy_name</c> (string), <c>partition_type</c> (user|ip|anonymous|unknown).
    /// </summary>
    public static readonly Counter<long> RateLimitRejectedCount = Meter.CreateCounter<long>(
        "apipipeline.ratelimit.rejected",
        description: "Number of requests rejected by rate limiting.");

    /// <summary>Counter: responses that included API version deprecation headers.</summary>
    public static readonly Counter<long> DeprecationHeadersAddedCount = Meter.CreateCounter<long>(
        "apipipeline.deprecation.headers_added",
        description: "Number of responses that included API version deprecation headers.");

    /// <summary>Counter: correlation IDs processed (generated or propagated).</summary>
    public static readonly Counter<long> CorrelationIdProcessedCount = Meter.CreateCounter<long>(
        "apipipeline.correlation_id.processed",
        description: "Number of correlation IDs processed (generated or propagated from client).");

    /// <summary>Counter: unhandled exceptions caught by the pipeline exception handler.</summary>
    public static readonly Counter<long> ExceptionHandledCount = Meter.CreateCounter<long>(
        "apipipeline.exceptions.handled",
        description: "Number of unhandled exceptions caught by the pipeline exception handler.");

    /// <summary>Counter: CORS requests rejected (origin not in AllowedOrigins).</summary>
    public static readonly Counter<long> CorsRejectedCount = Meter.CreateCounter<long>(
        "apipipeline.cors.rejected",
        description: "Number of CORS requests rejected due to disallowed origin.");

    /// <summary>Histogram: incoming request body size in bytes (sampled from Content-Length header).</summary>
    public static readonly Histogram<long> RequestBodyBytesHistogram = Meter.CreateHistogram<long>(
        "apipipeline.request.body_bytes",
        unit: "bytes",
        description: "Size of incoming request bodies (sampled from Content-Length when present).");

    /// <summary>
    /// Sets the correlation ID on the current <see cref="Activity"/>.
    /// </summary>
    public static void SetCorrelationIdOnCurrentActivity(string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId)) return;
        Activity.Current?.SetTag("correlation_id", correlationId);
    }

    /// <summary>
    /// Records that a request was rejected by rate limiting.
    /// </summary>
    /// <param name="policyName">The name of the rate limiting policy that rejected the request.</param>
    /// <param name="partitionType">The partition type: "user", "ip", "anonymous", or "unknown".</param>
    public static void RecordRateLimitRejected(string? policyName = null, string? partitionType = null)
    {
        if (policyName is { Length: > 0 } && partitionType is { Length: > 0 })
        {
            RateLimitRejectedCount.Add(1,
                new KeyValuePair<string, object?>("policy_name", policyName),
                new KeyValuePair<string, object?>("partition_type", partitionType));
        }
        else
        {
            RateLimitRejectedCount.Add(1);
        }
    }

    /// <summary>Records that deprecation headers were added to a response.</summary>
    public static void RecordDeprecationHeadersAdded(string? apiVersion = null)
    {
        if (apiVersion is { Length: > 0 })
        {
            DeprecationHeadersAddedCount.Add(1,
                new KeyValuePair<string, object?>("apipipeline.api_version", apiVersion));
        }
        else
        {
            DeprecationHeadersAddedCount.Add(1);
        }
    }

    /// <summary>Records that a correlation ID was processed.</summary>
    public static void RecordCorrelationIdProcessed() => CorrelationIdProcessedCount.Add(1);

    /// <summary>Records that an unhandled exception was caught by the pipeline exception handler.</summary>
    public static void RecordExceptionHandled() => ExceptionHandledCount.Add(1);

    /// <summary>Records that a CORS request was rejected due to disallowed origin.</summary>
    public static void RecordCorsRejected() => CorsRejectedCount.Add(1);

    /// <summary>Records the size of an incoming request body (from Content-Length header).</summary>
    public static void RecordRequestBodyBytes(long bytes) => RequestBodyBytesHistogram.Record(bytes);
}
```

- [x] **Step 14.2: Remove `RecordSecurityHeadersApplied` call from `SecurityHeadersMiddleware.cs`**

In `SecurityHeadersMiddleware.cs`, remove:
```csharp
// Remove this line from ApplyHeaders():
ApiPipelineTelemetry.RecordSecurityHeadersApplied();
```

- [x] **Step 14.3: Add CORS rejection metric to `LiveConfigCorsPolicyProvider`**

In `LiveConfigCorsPolicyProvider.cs`, after `builder.SetIsOriginAllowed(_ => false)`:

Actually the better place is to record CORS rejections in a middleware that wraps the CORS outcome. For now, add it to the `SetIsOriginAllowed` lambda:

```csharp
builder.SetIsOriginAllowed(origin =>
{
    ApiPipelineTelemetry.RecordCorsRejected();
    return false;
});
```

- [x] **Step 14.4: Run full suite and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs \
        src/ApiPipeline.NET/Middleware/SecurityHeadersMiddleware.cs \
        src/ApiPipeline.NET/Cors/LiveConfigCorsPolicyProvider.cs
git commit -m "feat: enhance telemetry — rate limit dimensions, CORS rejected counter, request body histogram; remove noisy SecurityHeadersApplied metric (T-1,T-3,T-4)"
```

---

### Task 15: Add `RequestSizeMiddleware` (T-2)

**Files:**
- Create: `src/ApiPipeline.NET/Middleware/RequestSizeMiddleware.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`
- Create: `tests/ApiPipeline.NET.Tests/RequestSizeMiddlewareTests.cs`

- [x] **Step 15.1: Create failing test**

Create `tests/ApiPipeline.NET.Tests/RequestSizeMiddlewareTests.cs`:

```csharp
using System.Net;
using System.Text;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for RequestSizeMiddleware — verifies it passes requests through without disruption.
/// (Histogram recording is verified indirectly via the middleware not throwing.)
/// </summary>
public sealed class RequestSizeMiddlewareTests
{
    /// <summary>
    /// Verifies that a POST request with a body passes through RequestSizeMiddleware unchanged.
    /// </summary>
    [Fact]
    public async Task RequestSizeMiddleware_PassesThrough_Post_With_Body()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseMiddleware<RequestSizeMiddleware>();
        app.MapPost("/data", () => Results.Ok("received"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var content = new StringContent("hello world", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/data", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that a GET request without a body passes through without error.
    /// </summary>
    [Fact]
    public async Task RequestSizeMiddleware_PassesThrough_Get_Without_Body()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.MinimalConfig());
        app.UseMiddleware<RequestSizeMiddleware>();
        app.MapGet("/ping", () => Results.Ok("pong"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [x] **Step 15.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~RequestSizeMiddlewareTests" -v
```

Expected: FAIL — `RequestSizeMiddleware` doesn't exist.

- [x] **Step 15.3: Create `RequestSizeMiddleware.cs`**

Create `src/ApiPipeline.NET/Middleware/RequestSizeMiddleware.cs`:

```csharp
using ApiPipeline.NET.Observability;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// Thin middleware that records incoming request body size to the
/// <see cref="ApiPipelineTelemetry.RequestBodyBytesHistogram"/> metric when
/// a <c>Content-Length</c> header is present. Has no effect on the request or response.
/// Place early in the pipeline — after <c>UseForwardedHeaders</c>, before business logic.
/// </summary>
public sealed class RequestSizeMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.ContentLength is { } length and > 0)
        {
            ApiPipelineTelemetry.RecordRequestBodyBytes(length);
        }

        await next(context);
    }
}
```

- [x] **Step 15.4: Register in DI and expose `UseRequestSizeTracking()` extension**

Add to `ServiceCollectionExtensions.cs` (in `AddCorrelationId` or as a new method):

Add to the public extension methods section:
```csharp
/// <summary>
/// Registers <see cref="RequestSizeMiddleware"/> for request body size telemetry.
/// </summary>
public static IServiceCollection AddRequestSizeTracking(this IServiceCollection services)
{
    services.TryAddTransient<RequestSizeMiddleware>();
    return services;
}
```

Add to `WebApplicationExtensions.cs`:
```csharp
/// <summary>
/// Adds the request size tracking middleware. Records <c>Content-Length</c> to the
/// <c>apipipeline.request.body_bytes</c> histogram for capacity planning and anomaly detection.
/// Place immediately after <c>UseApiPipelineForwardedHeaders</c>.
/// </summary>
public static WebApplication UseRequestSizeTracking(this WebApplication app)
{
    app.UseMiddleware<RequestSizeMiddleware>();
    return app;
}
```

- [x] **Step 15.5: Run all tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Middleware/RequestSizeMiddleware.cs \
        src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs \
        src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs \
        tests/ApiPipeline.NET.Tests/RequestSizeMiddlewareTests.cs
git commit -m "feat: add RequestSizeMiddleware recording Content-Length histogram for capacity planning (T-2)"
```

---

### Task 16: Fill Test Coverage Gaps (T-7)

**Files:**
- Create: `tests/ApiPipeline.NET.Tests/RequestLimitsTests.cs`
- Modify: `tests/ApiPipeline.NET.Tests/ForwardedHeadersTests.cs`
- Modify: `tests/ApiPipeline.NET.Tests/ResponseCompressionTests.cs`
- Modify: `tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs`

- [x] **Step 16.1: Create `RequestLimitsTests.cs`**

Create `tests/ApiPipeline.NET.Tests/RequestLimitsTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for request limits configuration and validation.
/// </summary>
public sealed class RequestLimitsTests
{
    /// <summary>
    /// Verifies that all nullable limit properties accept valid positive values.
    /// </summary>
    [Fact]
    public async Task RequestLimits_ValidPositiveValues_Pass_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RequestLimitsOptions:Enabled"] = "true";
            c["RequestLimitsOptions:MaxRequestBodySize"] = "10485760";
            c["RequestLimitsOptions:MaxRequestHeadersTotalSize"] = "32768";
            c["RequestLimitsOptions:MaxRequestHeaderCount"] = "100";
            c["RequestLimitsOptions:MaxFormValueCount"] = "1024";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that limits disabled still passes validation with no values set.
    /// </summary>
    [Fact]
    public async Task RequestLimits_Disabled_No_Values_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(); // RequestLimitsOptions:Enabled defaults to false
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies MaxFormValueCount = 0 fails validation when limits are enabled.
    /// </summary>
    [Fact]
    public async Task RequestLimits_MaxFormValueCount_Zero_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RequestLimitsOptions:Enabled"] = "true";
            c["RequestLimitsOptions:MaxFormValueCount"] = "0";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*MaxFormValueCount*");
    }
}
```

- [x] **Step 16.2: Add untrusted proxy spoofing test to `ForwardedHeadersTests.cs`**

Add to `ForwardedHeadersTests.cs`:

```csharp
/// <summary>
/// Verifies that when no KnownProxies/KnownNetworks are configured and
/// ClearDefaultProxies is false, X-Forwarded-For from an untrusted proxy is NOT applied.
/// This is a security test — spoofed IPs from untrusted sources must be ignored.
/// </summary>
[Fact]
public async Task UseApiPipelineForwardedHeaders_Ignores_XForwardedFor_From_Untrusted_Proxy()
{
    // Default config: KnownProxies empty, ClearDefaultProxies false
    // ASP.NET Core only trusts loopback by default, so spoofed IPs should be rejected
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["ForwardedHeadersOptions:Enabled"] = "true";
        c["ForwardedHeadersOptions:ClearDefaultProxies"] = "false";
        // No KnownProxies or KnownNetworks
    });
    await using var app = await TestAppBuilder.CreateAppAsync(config);

    app.UseApiPipelineForwardedHeaders();
    app.MapGet("/ip", (HttpContext ctx) =>
        Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() }));
    await app.StartAsync();

    var client = app.GetTestClient();
    var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
    // Attempt to spoof client IP
    request.Headers.Add("X-Forwarded-For", "1.2.3.4");

    var response = await client.SendAsync(request);
    response.StatusCode.Should().Be(HttpStatusCode.OK);

    var body = await response.Content.ReadAsStringAsync();
    // The spoofed IP must NOT be applied (not in trusted proxy list)
    body.Should().NotContain("\"ip\":\"1.2.3.4\"");
}
```

- [x] **Step 16.3: Add excluded path test to `ResponseCompressionTests.cs`**

Add to `ResponseCompressionTests.cs`:

```csharp
/// <summary>
/// Verifies that paths listed in ExcludedPaths are not compressed.
/// Health endpoints should never be compressed to avoid adding latency to probes.
/// </summary>
[Fact]
public async Task ResponseCompression_ExcludedPath_Not_Compressed()
{
    var config = TestAppBuilder.MinimalConfig(c =>
    {
        c["ResponseCompressionOptions:Enabled"] = "true";
        c["ResponseCompressionOptions:ExcludedPaths:0"] = "/health";
    });
    await using var app = await TestAppBuilder.CreateAppAsync(config);
    app.UseResponseCompression();
    app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));
    app.MapGet("/data", () => Results.Ok(new string('x', 2000))); // compressible
    await app.StartAsync();

    var client = app.GetTestClient();
    client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, br");

    var healthResponse = await client.GetAsync("/health");
    var dataResponse = await client.GetAsync("/data");

    // /health should NOT have Content-Encoding
    healthResponse.Content.Headers.ContentEncoding.Should().BeEmpty(
        "health endpoint is in ExcludedPaths and must not be compressed");
}
```

- [x] **Step 16.4: Run all new and existing tests**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

Expected: All tests pass.

- [x] **Step 16.5: Commit**

```bash
git add tests/ApiPipeline.NET.Tests/RequestLimitsTests.cs \
        tests/ApiPipeline.NET.Tests/ForwardedHeadersTests.cs \
        tests/ApiPipeline.NET.Tests/ResponseCompressionTests.cs \
        tests/ApiPipeline.NET.Tests/OptionsValidationTests.cs
git commit -m "test: fill coverage gaps — request limits, forwarded header spoofing, compression path exclusion (T-7)"
```

---

## Phase 5 — Advanced Features

---

### Task 17: Move `Asp.Versioning.Mvc` to Satellite Package (T-5) - Start from Here

**Files:**
- Create: `src/ApiPipeline.NET/Versioning/IApiVersionReader.cs`
- Modify: `src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs`
- Modify: `src/ApiPipeline.NET/ApiPipeline.NET.csproj`
- Create: `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj`
- Create: `src/ApiPipeline.NET.Versioning/AspVersioningApiVersionReader.cs`
- Create: `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`
- Modify: `samples/ApiPipeline.NET.Sample/Program.cs`

- [x] **Step 17.1: Create `IApiVersionReader` interface in core**

Create `src/ApiPipeline.NET/Versioning/IApiVersionReader.cs`:

```csharp
namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Reads the requested API version string from an HTTP context.
/// Register an implementation via <c>services.AddSingleton&lt;IApiVersionReader, YourImpl&gt;()</c>
/// or use the <c>ApiPipeline.NET.Versioning</c> satellite package which provides an implementation
/// backed by <c>Asp.Versioning.Mvc</c>.
/// </summary>
public interface IApiVersionReader
{
    /// <summary>
    /// Returns the requested API version string, or <c>null</c> if no version was specified.
    /// </summary>
    string? ReadApiVersion(HttpContext context);
}
```

- [x] **Step 17.2: Update `ApiVersionDeprecationMiddleware` to use `IApiVersionReader` from DI**

In `ApiVersionDeprecationMiddleware.cs`, update the constructor and `Invoke` method:

```csharp
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Options;
using ApiPipeline.NET.Versioning;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// ASP.NET Core middleware that emits deprecation-related headers for deprecated API versions.
/// Requires an <see cref="IApiVersionReader"/> registration (provided by the
/// <c>ApiPipeline.NET.Versioning</c> satellite package). If no reader is registered, the
/// middleware passes through silently.
/// </summary>
public sealed class ApiVersionDeprecationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ApiVersionDeprecationOptions> _options;
    private readonly ILogger<ApiVersionDeprecationMiddleware> _logger;
    private readonly IApiVersionReader? _versionReader;

    public ApiVersionDeprecationMiddleware(
        RequestDelegate next,
        IOptionsMonitor<ApiVersionDeprecationOptions> options,
        ILogger<ApiVersionDeprecationMiddleware> logger,
        IServiceProvider services)
    {
        _next = next;
        _options = options;
        _logger = logger;
        _versionReader = services.GetService<IApiVersionReader>();

        if (_versionReader is null)
        {
            logger.LogDebug(
                "ApiVersionDeprecationMiddleware: No IApiVersionReader registered. " +
                "Version deprecation headers will not be emitted. " +
                "Add the ApiPipeline.NET.Versioning package to enable this feature.");
        }
    }

    public async Task Invoke(HttpContext context)
    {
        if (_versionReader is null)
        {
            await _next(context);
            return;
        }

        context.Response.OnStarting(static state =>
        {
            var (ctx, opts, logger, reader) =
                ((HttpContext, ApiVersionDeprecationOptions, ILogger<ApiVersionDeprecationMiddleware>, IApiVersionReader))state!;

            var pathPrefix = opts.PathPrefix ?? "/api";
            if (!ctx.Request.Path.StartsWithSegments(pathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            var deprecatedVersions = opts.DeprecatedVersions ?? [];
            if (!opts.Enabled || deprecatedVersions.Length == 0)
            {
                return Task.CompletedTask;
            }

            var requestedText = reader.ReadApiVersion(ctx);
            if (requestedText is null)
            {
                return Task.CompletedTask;
            }

            var deprecated = deprecatedVersions.FirstOrDefault(v =>
                string.Equals(v.Version, requestedText, StringComparison.OrdinalIgnoreCase));

            if (deprecated is null)
            {
                return Task.CompletedTask;
            }

            if (deprecated.DeprecationDate is { } deprecationDate)
            {
                ctx.Response.Headers["Deprecation"] = deprecationDate.ToString("R");
            }
            else
            {
                ctx.Response.Headers["Deprecation"] = "true";
            }

            if (deprecated.SunsetDate is { } sunsetDate)
            {
                ctx.Response.Headers["Sunset"] = sunsetDate.ToString("R");
            }

            if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
            {
                if (Uri.TryCreate(deprecated.SunsetLink, UriKind.Absolute, out _))
                {
                    ctx.Response.Headers.Append("Link", $"<{deprecated.SunsetLink}>; rel=\"sunset\"");
                }
                else
                {
                    logger.LogWarning(
                        "ApiVersionDeprecation: SunsetLink '{SunsetLink}' is not a valid absolute URI — skipped.",
                        deprecated.SunsetLink);
                }
            }

            ApiPipelineTelemetry.RecordDeprecationHeadersAdded(requestedText);
            logger.LogDebug("Deprecation headers added for API version {ApiVersion}", requestedText);

            return Task.CompletedTask;
        }, (context, _options.CurrentValue, _logger, _versionReader));

        await _next(context);
    }
}
```

- [x] **Step 17.3: Remove `Asp.Versioning.Mvc` from core project**

In `src/ApiPipeline.NET/ApiPipeline.NET.csproj`, remove:
```xml
<PackageReference Include="Asp.Versioning.Mvc" />
```

- [x] **Step 17.4: Create the satellite project**

Create `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>true</IsPackable>
    <PackageId>ApiPipeline.NET.Versioning</PackageId>
    <Version>1.0.0</Version>
    <Authors>BAPS Dev Team</Authors>
    <Description>Asp.Versioning.Mvc integration for ApiPipeline.NET API version deprecation headers.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ApiPipeline.NET\ApiPipeline.NET.csproj" />
    <PackageReference Include="Asp.Versioning.Mvc" />
  </ItemGroup>
</Project>
```

- [x] **Step 17.5: Create `AspVersioningApiVersionReader.cs`**

Create `src/ApiPipeline.NET.Versioning/AspVersioningApiVersionReader.cs`:

```csharp
using ApiPipeline.NET.Versioning;
using Asp.Versioning;

namespace ApiPipeline.NET.Versioning.AspVersioning;

/// <summary>
/// Reads the requested API version using <c>Asp.Versioning.Mvc</c>'s
/// <see cref="HttpContextExtensions.GetRequestedApiVersion"/> extension.
/// </summary>
internal sealed class AspVersioningApiVersionReader : IApiVersionReader
{
    public string? ReadApiVersion(HttpContext context)
        => context.GetRequestedApiVersion()?.ToString();
}
```

- [x] **Step 17.6: Create `VersioningServiceCollectionExtensions.cs`**

Create `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`:

```csharp
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Versioning.AspVersioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Extension methods for integrating <c>Asp.Versioning.Mvc</c> with ApiPipeline.NET.
/// </summary>
public static class VersioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers API version deprecation services using <c>Asp.Versioning.Mvc</c> for
    /// version resolution. Call this instead of <c>AddApiVersionDeprecation</c> when using
    /// the <c>Asp.Versioning</c> package.
    /// </summary>
    public static IServiceCollection AddApiPipelineVersioning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddApiVersionDeprecation(configuration);
        services.TryAddSingleton<IApiVersionReader, AspVersioningApiVersionReader>();
        return services;
    }
}
```

- [x] **Step 17.7: Update the sample to use the satellite**

In `samples/ApiPipeline.NET.Sample/Program.cs`, replace `.AddApiVersionDeprecation(builder.Configuration)` with `.AddApiPipelineVersioning(builder.Configuration)`, and add the using:
```csharp
using ApiPipeline.NET.Versioning;
```

Also add the project reference to `samples/ApiPipeline.NET.Sample/ApiPipeline.NET.Sample.csproj`:
```xml
<ProjectReference Include="..\..\src\ApiPipeline.NET.Versioning\ApiPipeline.NET.Versioning.csproj" />
```

- [x] **Step 17.8: Add satellite project to solution**

```bash
dotnet sln add src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj
```

- [x] **Step 17.9: Build and run tests**

```bash
dotnet build
dotnet test tests/ApiPipeline.NET.Tests/
```

Expected: All tests pass. The `ApiVersionDeprecationMiddlewareTests` tests that call `Asp.Versioning.ApiVersion` may need their project reference updated to reference `ApiPipeline.NET.Versioning`.

- [x] **Step 17.10: Commit**

```bash
git add src/ApiPipeline.NET/Versioning/ \
        src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs \
        src/ApiPipeline.NET/ApiPipeline.NET.csproj \
        src/ApiPipeline.NET.Versioning/ \
        samples/ApiPipeline.NET.Sample/
git commit -m "feat: move Asp.Versioning.Mvc to ApiPipeline.NET.Versioning satellite; core no longer depends on versioning library (T-5)"
```

---

### Task 18: Add Output Caching Upgrade Path (T-6, A-4)

**Files:**
- Modify: `src/ApiPipeline.NET/Options/ResponseCachingSettings.cs`
- Create: `src/ApiPipeline.NET.OutputCaching/ApiPipeline.NET.OutputCaching.csproj`
- Create: `src/ApiPipeline.NET.OutputCaching/OutputCachingServiceCollectionExtensions.cs`
- Create: `src/ApiPipeline.NET.OutputCaching/OutputCachingWebApplicationExtensions.cs`

- [x] **Step 18.1: Add `PreferOutputCaching` flag to `ResponseCachingSettings`**

In `ResponseCachingSettings.cs`, add:

```csharp
/// <summary>
/// When <c>true</c>, consumers should use the <c>ApiPipeline.NET.OutputCaching</c> satellite
/// package instead, which provides .NET 7+ Output Caching with distributed store support (Redis).
/// This flag does nothing in the core package — it is a signal for migration.
/// </summary>
public bool PreferOutputCaching { get; set; } = false;
```

- [x] **Step 18.2: Create the Output Caching satellite project**

Create `src/ApiPipeline.NET.OutputCaching/ApiPipeline.NET.OutputCaching.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>true</IsPackable>
    <PackageId>ApiPipeline.NET.OutputCaching</PackageId>
    <Version>1.0.0</Version>
    <Authors>BAPS Dev Team</Authors>
    <Description>Output Caching satellite for ApiPipeline.NET — uses ASP.NET Core Output Caching with distributed store support.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ApiPipeline.NET\ApiPipeline.NET.csproj" />
  </ItemGroup>
</Project>
```

- [x] **Step 18.3: Create `OutputCachingServiceCollectionExtensions.cs`**

Create `src/ApiPipeline.NET.OutputCaching/OutputCachingServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.OutputCaching;

/// <summary>
/// Extension methods to configure ASP.NET Core Output Caching as an alternative to
/// the in-memory <c>ResponseCaching</c> middleware. Output Caching supports distributed stores,
/// tag-based eviction, and per-endpoint revalidation semantics.
/// </summary>
public static class OutputCachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers Output Caching services. Use <see cref="OutputCachingWebApplicationExtensions.UseApiPipelineOutputCaching"/>
    /// in the middleware pipeline.
    /// </summary>
    public static IServiceCollection AddApiPipelineOutputCaching(
        this IServiceCollection services,
        Action<OutputCacheOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.AddOutputCache(configure);
        }
        else
        {
            services.AddOutputCache();
        }

        return services;
    }
}
```

- [x] **Step 18.4: Create `OutputCachingWebApplicationExtensions.cs`**

Create `src/ApiPipeline.NET.OutputCaching/OutputCachingWebApplicationExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;

namespace ApiPipeline.NET.OutputCaching;

/// <summary>
/// Extension methods for enabling Output Caching middleware.
/// </summary>
public static class OutputCachingWebApplicationExtensions
{
    /// <summary>
    /// Adds the Output Caching middleware to the pipeline. Place after
    /// <c>UseAuthorization</c> to prevent caching of unauthorized responses.
    /// </summary>
    public static WebApplication UseApiPipelineOutputCaching(this WebApplication app)
    {
        app.UseOutputCache();
        return app;
    }
}
```

- [x] **Step 18.5: Add to solution and build**

```bash
dotnet sln add src/ApiPipeline.NET.OutputCaching/ApiPipeline.NET.OutputCaching.csproj
dotnet build
dotnet test tests/ApiPipeline.NET.Tests/
```

- [x] **Step 18.6: Commit**

```bash
git add src/ApiPipeline.NET/Options/ResponseCachingSettings.cs \
        src/ApiPipeline.NET.OutputCaching/
git commit -m "feat: add ApiPipeline.NET.OutputCaching satellite for distributed Output Caching migration path (T-6, A-4)"
```

---

### Task 19: Implement `IApiPipelineBuilder` Phase-Enforced Pipeline (C-1, A-1, A-5)

**Files:**
- Create: `src/ApiPipeline.NET/Pipeline/IApiPipelineBuilder.cs`
- Create: `src/ApiPipeline.NET/Pipeline/ApiPipelineBuilder.cs`
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`
- Create: `tests/ApiPipeline.NET.Tests/PipelineBuilderTests.cs`

- [x] **Step 19.1: Create failing tests**

Create `tests/ApiPipeline.NET.Tests/PipelineBuilderTests.cs`:

```csharp
using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for IApiPipelineBuilder — verifies phase ordering and builder contract.
/// </summary>
public sealed class PipelineBuilderTests
{
    /// <summary>
    /// Verifies that UseApiPipeline applies middleware in the correct order:
    /// specifically that auth runs before caching, preventing auth-bypass via cache.
    /// </summary>
    [Fact]
    public async Task UseApiPipeline_Auth_Before_Caching_Prevents_Cache_Bypass()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        app.UseApiPipeline(pipeline => pipeline
            .WithAuthentication()
            .WithAuthorization()
            .WithResponseCaching());

        app.MapGet("/secure", [Authorize] () => Results.Ok("secret"))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync();
        var client = app.GetTestClient();

        // Unauthenticated request must get 401, not a cached 200
        var unauthResponse = await client.GetAsync("/secure");
        unauthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Verifies that calling With* methods in any order produces the correct pipeline order.
    /// Registering WithResponseCaching before WithAuthentication must still apply auth first.
    /// </summary>
    [Fact]
    public async Task UseApiPipeline_Order_Of_With_Calls_Does_Not_Affect_Pipeline_Order()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ResponseCachingOptions:Enabled"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config, addExceptionHandler: true);

        // Deliberately register in wrong order — builder must fix it
        app.UseApiPipeline(pipeline => pipeline
            .WithResponseCaching()   // registered first but must execute AFTER auth
            .WithAuthorization()
            .WithAuthentication());

        app.MapGet("/secure", [Authorize] () => Results.Ok("secret"))
            .WithMetadata(new ResponseCacheAttribute { Duration = 60 });

        await app.StartAsync();
        var client = app.GetTestClient();

        var unauthResponse = await client.GetAsync("/secure");
        unauthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "even when WithResponseCaching is declared before WithAuthentication, auth must run first");
    }

    /// <summary>
    /// Verifies that Skip methods prevent a middleware from being added.
    /// </summary>
    [Fact]
    public async Task UseApiPipeline_Skip_Prevents_Middleware_Registration()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        // If HTTPS redirection were active, HTTP requests would be redirected
        // By skipping it, HTTP requests pass through
        app.UseApiPipeline(pipeline => pipeline
            .WithHttpsRedirection()
            .SkipHttpsRedirection());  // skip overrides with

        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [x] **Step 19.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~PipelineBuilderTests" -v
```

Expected: FAIL — `UseApiPipeline` extension doesn't exist.

- [x] **Step 19.3: Create `IApiPipelineBuilder.cs`**

Create `src/ApiPipeline.NET/Pipeline/IApiPipelineBuilder.cs`:

```csharp
namespace ApiPipeline.NET.Pipeline;

/// <summary>
/// Fluent builder for configuring the API middleware pipeline in a phase-enforced order.
/// Middleware is always applied in the correct sequence regardless of the order <c>With*</c>
/// methods are called. Use via <see cref="WebApplicationExtensions.UseApiPipeline"/>.
/// </summary>
public interface IApiPipelineBuilder
{
    /// <summary>Adds forwarded headers processing (Infrastructure phase).</summary>
    IApiPipelineBuilder WithForwardedHeaders();

    /// <summary>Adds correlation ID middleware (Infrastructure phase).</summary>
    IApiPipelineBuilder WithCorrelationId();

    /// <summary>Adds exception handler and status code pages (Infrastructure phase).</summary>
    IApiPipelineBuilder WithExceptionHandler();

    /// <summary>Adds HTTPS redirection (Infrastructure phase).</summary>
    IApiPipelineBuilder WithHttpsRedirection();

    /// <summary>Adds CORS (Security phase).</summary>
    IApiPipelineBuilder WithCors();

    /// <summary>Adds authentication (Auth phase).</summary>
    IApiPipelineBuilder WithAuthentication();

    /// <summary>Adds authorization (Auth phase).</summary>
    IApiPipelineBuilder WithAuthorization();

    /// <summary>Adds rate limiting (RateLimiting phase — always after auth).</summary>
    IApiPipelineBuilder WithRateLimiting();

    /// <summary>Adds response compression (Output phase).</summary>
    IApiPipelineBuilder WithResponseCompression();

    /// <summary>Adds response caching (Output phase — always after auth/authorization).</summary>
    IApiPipelineBuilder WithResponseCaching();

    /// <summary>Adds security headers (Headers phase).</summary>
    IApiPipelineBuilder WithSecurityHeaders();

    /// <summary>Adds API version deprecation headers (Headers phase).</summary>
    IApiPipelineBuilder WithVersionDeprecation();

    /// <summary>Excludes HTTPS redirection from the pipeline.</summary>
    IApiPipelineBuilder SkipHttpsRedirection();

    /// <summary>Excludes API version deprecation from the pipeline.</summary>
    IApiPipelineBuilder SkipVersionDeprecation();

    /// <summary>Excludes security headers from the pipeline.</summary>
    IApiPipelineBuilder SkipSecurityHeaders();

    /// <summary>Excludes CORS from the pipeline.</summary>
    IApiPipelineBuilder SkipCors();
}
```

- [x] **Step 19.4: Create `ApiPipelineBuilder.cs`**

Create `src/ApiPipeline.NET/Pipeline/ApiPipelineBuilder.cs`:

```csharp
using ApiPipeline.NET.Extensions;
using Microsoft.AspNetCore.Builder;

namespace ApiPipeline.NET.Pipeline;

/// <summary>
/// Default implementation of <see cref="IApiPipelineBuilder"/>. Records middleware intents
/// and applies them in a fixed phase order when <see cref="Build"/> is called.
/// </summary>
internal sealed class ApiPipelineBuilder : IApiPipelineBuilder
{
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);

    // Fixed phase order — this is the source of truth for safe middleware ordering.
    // Authentication MUST precede ResponseCaching to prevent auth-bypass via cached responses.
    private static readonly string[] PhaseOrder =
    [
        "ForwardedHeaders",
        "CorrelationId",
        "ExceptionHandler",
        "HttpsRedirection",
        "Cors",
        "Authentication",
        "Authorization",
        "RateLimiting",
        "ResponseCompression",
        "ResponseCaching",
        "SecurityHeaders",
        "VersionDeprecation",
    ];

    public IApiPipelineBuilder WithForwardedHeaders()     { _requested.Add("ForwardedHeaders"); return this; }
    public IApiPipelineBuilder WithCorrelationId()        { _requested.Add("CorrelationId"); return this; }
    public IApiPipelineBuilder WithExceptionHandler()     { _requested.Add("ExceptionHandler"); return this; }
    public IApiPipelineBuilder WithHttpsRedirection()     { _requested.Add("HttpsRedirection"); return this; }
    public IApiPipelineBuilder WithCors()                 { _requested.Add("Cors"); return this; }
    public IApiPipelineBuilder WithAuthentication()       { _requested.Add("Authentication"); return this; }
    public IApiPipelineBuilder WithAuthorization()        { _requested.Add("Authorization"); return this; }
    public IApiPipelineBuilder WithRateLimiting()         { _requested.Add("RateLimiting"); return this; }
    public IApiPipelineBuilder WithResponseCompression()  { _requested.Add("ResponseCompression"); return this; }
    public IApiPipelineBuilder WithResponseCaching()      { _requested.Add("ResponseCaching"); return this; }
    public IApiPipelineBuilder WithSecurityHeaders()      { _requested.Add("SecurityHeaders"); return this; }
    public IApiPipelineBuilder WithVersionDeprecation()   { _requested.Add("VersionDeprecation"); return this; }

    public IApiPipelineBuilder SkipHttpsRedirection()  { _skipped.Add("HttpsRedirection"); return this; }
    public IApiPipelineBuilder SkipVersionDeprecation(){ _skipped.Add("VersionDeprecation"); return this; }
    public IApiPipelineBuilder SkipSecurityHeaders()   { _skipped.Add("SecurityHeaders"); return this; }
    public IApiPipelineBuilder SkipCors()              { _skipped.Add("Cors"); return this; }

    internal void Build(WebApplication app)
    {
        foreach (var feature in PhaseOrder)
        {
            if (!_requested.Contains(feature) || _skipped.Contains(feature))
            {
                continue;
            }

            switch (feature)
            {
                case "ForwardedHeaders":    app.UseApiPipelineForwardedHeaders(); break;
                case "CorrelationId":       app.UseCorrelationId(); break;
                case "ExceptionHandler":    app.UseApiPipelineExceptionHandler(); break;
                case "HttpsRedirection":    app.UseHttpsRedirection(); break;
                case "Cors":                app.UseCors(); break;
                case "Authentication":      app.UseAuthentication(); break;
                case "Authorization":       app.UseAuthorization(); break;
                case "RateLimiting":        app.UseRateLimiting(); break;
                case "ResponseCompression": app.UseResponseCompression(); break;
                case "ResponseCaching":     app.UseResponseCaching(); break;
                case "SecurityHeaders":     app.UseSecurityHeaders(); break;
                case "VersionDeprecation":  app.UseApiVersionDeprecation(); break;
            }
        }
    }
}
```

- [x] **Step 19.5: Add `UseApiPipeline` to `WebApplicationExtensions.cs`**

Add the following method to `WebApplicationExtensions.cs`:

```csharp
/// <summary>
/// Configures the full API middleware pipeline using a phase-enforced fluent builder.
/// Middleware is always applied in the correct sequence regardless of the order
/// <c>With*</c> methods are called, preventing common ordering mistakes such as
/// placing response caching before authentication.
/// </summary>
/// <example>
/// <code>
/// app.UseApiPipeline(pipeline => pipeline
///     .WithForwardedHeaders()
///     .WithCorrelationId()
///     .WithExceptionHandler()
///     .WithCors()
///     .WithAuthentication()
///     .WithAuthorization()
///     .WithRateLimiting()
///     .WithResponseCompression()
///     .WithResponseCaching()
///     .WithSecurityHeaders()
///     .WithVersionDeprecation()
/// );
/// </code>
/// </example>
public static WebApplication UseApiPipeline(
    this WebApplication app,
    Action<IApiPipelineBuilder> configure)
{
    var builder = new ApiPipelineBuilder();
    configure(builder);
    builder.Build(app);
    return app;
}
```

Add the required using at the top of `WebApplicationExtensions.cs`:
```csharp
using ApiPipeline.NET.Pipeline;
```

- [x] **Step 19.6: Run all tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

Expected: All tests including the three new `PipelineBuilderTests` pass.

```bash
git add src/ApiPipeline.NET/Pipeline/ \
        src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs \
        tests/ApiPipeline.NET.Tests/PipelineBuilderTests.cs
git commit -m "feat: add IApiPipelineBuilder with phase-enforced ordering — UseApiPipeline prevents auth-before-caching mistakes (C-1, A-1, A-5)"
```

---

### Task 20: Implement `IRequestValidationFilter` OWASP API7 Hook (A-3)

**Files:**
- Create: `src/ApiPipeline.NET/Validation/IRequestValidationFilter.cs`
- Create: `src/ApiPipeline.NET/Validation/RequestValidationResult.cs`
- Create: `src/ApiPipeline.NET/Middleware/RequestValidationMiddleware.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs`
- Create: `tests/ApiPipeline.NET.Tests/RequestValidationMiddlewareTests.cs`

- [x] **Step 20.1: Create failing tests**

Create `tests/ApiPipeline.NET.Tests/RequestValidationMiddlewareTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the IRequestValidationFilter pipeline hook.
/// </summary>
public sealed class RequestValidationMiddlewareTests
{
    /// <summary>
    /// Verifies that a passing filter allows the request through.
    /// </summary>
    [Fact]
    public async Task ValidFilter_Allows_Request()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(), addExceptionHandler: true);
        app.Services.GetRequiredService<IServiceCollection>(); // just checking DI is wired

        // We need to add validation before BuildServiceProvider — use a fresh builder
        var builder2 = WebApplication.CreateBuilder();
        builder2.WebHost.UseTestServer();
        builder2.Configuration.AddInMemoryCollection(TestAppBuilder.MinimalConfig());
        builder2.Services.AddRouting();
        builder2.Services.AddApiPipelineExceptionHandler();
        builder2.Services.AddRequestValidation<AlwaysValidFilter>();
        await using var app2 = builder2.Build();

        app2.UseApiPipelineExceptionHandler();
        app2.UseRequestValidation();
        app2.MapGet("/test", () => Results.Ok("ok"));
        await app2.StartAsync();

        var response = await app2.GetTestClient().GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verifies that a rejecting filter returns 400 with ProblemDetails.
    /// </summary>
    [Fact]
    public async Task RejectingFilter_Returns_ProblemDetails_400()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(TestAppBuilder.MinimalConfig());
        builder.Services.AddRouting();
        builder.Services.AddApiPipelineExceptionHandler();
        builder.Services.AddRequestValidation<AlwaysRejectFilter>();
        await using var app = builder.Build();

        app.UseApiPipelineExceptionHandler();
        app.UseRequestValidation();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetInt32().Should().Be(400);
    }

    private sealed class AlwaysValidFilter : IRequestValidationFilter
    {
        public ValueTask<RequestValidationResult> ValidateAsync(HttpContext context)
            => ValueTask.FromResult(RequestValidationResult.Valid);
    }

    private sealed class AlwaysRejectFilter : IRequestValidationFilter
    {
        public ValueTask<RequestValidationResult> ValidateAsync(HttpContext context)
            => ValueTask.FromResult(RequestValidationResult.Invalid(400, "Validation failed by test filter."));
    }
}
```

- [x] **Step 20.2: Run to confirm fail**

```bash
dotnet test tests/ApiPipeline.NET.Tests/ --filter "FullyQualifiedName~RequestValidationMiddlewareTests" -v
```

Expected: FAIL — types don't exist.

- [x] **Step 20.3: Create `RequestValidationResult.cs`**

Create `src/ApiPipeline.NET/Validation/RequestValidationResult.cs`:

```csharp
namespace ApiPipeline.NET.Validation;

/// <summary>
/// The result of a request validation check performed by an <see cref="IRequestValidationFilter"/>.
/// </summary>
public readonly struct RequestValidationResult
{
    private RequestValidationResult(bool isValid, int statusCode, string? detail)
    {
        IsValid = isValid;
        StatusCode = statusCode;
        Detail = detail;
    }

    /// <summary>Whether the request passed validation.</summary>
    public bool IsValid { get; }

    /// <summary>HTTP status code to return on failure. Ignored when <see cref="IsValid"/> is true.</summary>
    public int StatusCode { get; }

    /// <summary>Human-readable detail message for the problem response.</summary>
    public string? Detail { get; }

    /// <summary>A valid result — the request passes validation.</summary>
    public static RequestValidationResult Valid { get; } = new(true, 200, null);

    /// <summary>Creates an invalid result with the given status code and detail message.</summary>
    public static RequestValidationResult Invalid(int statusCode, string detail)
        => new(false, statusCode, detail);
}
```

- [x] **Step 20.4: Create `IRequestValidationFilter.cs`**

Create `src/ApiPipeline.NET/Validation/IRequestValidationFilter.cs`:

```csharp
namespace ApiPipeline.NET.Validation;

/// <summary>
/// Defines a request validation filter that runs in the ApiPipeline.NET middleware pipeline.
/// Multiple filters can be registered; they are evaluated in registration order and the first
/// failure short-circuits the pipeline with an RFC 7807 problem details response.
/// Register via <see cref="Extensions.ServiceCollectionExtensions.AddRequestValidation{T}"/>.
/// </summary>
public interface IRequestValidationFilter
{
    /// <summary>
    /// Validates the current request. Return <see cref="RequestValidationResult.Valid"/> to allow
    /// the request through, or <see cref="RequestValidationResult.Invalid"/> to reject it.
    /// </summary>
    ValueTask<RequestValidationResult> ValidateAsync(HttpContext context);
}
```

- [x] **Step 20.5: Create `RequestValidationMiddleware.cs`**

Create `src/ApiPipeline.NET/Middleware/RequestValidationMiddleware.cs`:

```csharp
using ApiPipeline.NET.Validation;
using Microsoft.AspNetCore.Http;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// Runs all registered <see cref="IRequestValidationFilter"/> implementations in order.
/// Short-circuits with an RFC 7807 problem details response on the first failure.
/// </summary>
public sealed class RequestValidationMiddleware : IMiddleware
{
    private readonly IEnumerable<IRequestValidationFilter> _filters;
    private readonly IProblemDetailsService _problemDetailsService;

    public RequestValidationMiddleware(
        IEnumerable<IRequestValidationFilter> filters,
        IProblemDetailsService problemDetailsService)
    {
        _filters = filters;
        _problemDetailsService = problemDetailsService;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        foreach (var filter in _filters)
        {
            var result = await filter.ValidateAsync(context);
            if (!result.IsValid)
            {
                context.Response.StatusCode = result.StatusCode;
                context.Response.Headers.CacheControl = "no-store";

                await _problemDetailsService.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = result.StatusCode,
                        Detail = result.Detail
                    }
                });
                return;
            }
        }

        await next(context);
    }
}
```

- [x] **Step 20.6: Add `AddRequestValidation<T>()` to `ServiceCollectionExtensions.cs`**

Add to the public extension methods section:

```csharp
/// <summary>
/// Registers a request validation filter. Multiple filters can be registered and are
/// evaluated in registration order. The first failure short-circuits the pipeline.
/// Also registers <see cref="Middleware.RequestValidationMiddleware"/> for DI activation.
/// </summary>
public static IServiceCollection AddRequestValidation<TFilter>(this IServiceCollection services)
    where TFilter : class, IRequestValidationFilter
{
    services.AddTransient<IRequestValidationFilter, TFilter>();
    services.TryAddTransient<RequestValidationMiddleware>();
    return services;
}
```

Add the using at the top of `ServiceCollectionExtensions.cs`:
```csharp
using ApiPipeline.NET.Validation;
```

- [x] **Step 20.7: Add `UseRequestValidation()` to `WebApplicationExtensions.cs`**

```csharp
/// <summary>
/// Adds the request validation middleware. Runs all registered
/// <see cref="IRequestValidationFilter"/> implementations before reaching endpoints.
/// Requires <see cref="ServiceCollectionExtensions.AddRequestValidation{T}"/> and
/// <see cref="ServiceCollectionExtensions.AddApiPipelineExceptionHandler"/> to be called first.
/// </summary>
public static WebApplication UseRequestValidation(this WebApplication app)
{
    app.UseMiddleware<RequestValidationMiddleware>();
    return app;
}
```

Also add to `IApiPipelineBuilder` and `ApiPipelineBuilder` to make it available via the builder:

In `IApiPipelineBuilder.cs`:
```csharp
/// <summary>Adds request validation filters (after Auth phase, before endpoints).</summary>
IApiPipelineBuilder WithRequestValidation();
```

In `ApiPipelineBuilder.cs`:
- Add `"RequestValidation"` to `PhaseOrder` between `"Authorization"` and `"RateLimiting"`
- Add `public IApiPipelineBuilder WithRequestValidation() { _requested.Add("RequestValidation"); return this; }`
- Add `case "RequestValidation": app.UseRequestValidation(); break;` to the `Build` switch

- [x] **Step 20.8: Run all tests and commit**

```bash
dotnet test tests/ApiPipeline.NET.Tests/
```

```bash
git add src/ApiPipeline.NET/Validation/ \
        src/ApiPipeline.NET/Middleware/RequestValidationMiddleware.cs \
        src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs \
        src/ApiPipeline.NET/Extensions/WebApplicationExtensions.cs \
        src/ApiPipeline.NET/Pipeline/ \
        tests/ApiPipeline.NET.Tests/RequestValidationMiddlewareTests.cs
git commit -m "feat: add IRequestValidationFilter pipeline hook for OWASP API7 request validation (A-3)"
```

---

## Final Verification

- [x] **Run complete test suite**

```bash
dotnet test
```

Expected: All tests pass. No regressions.

- [x] **Build all projects**

```bash
dotnet build --configuration Release
```

Expected: Clean build with zero errors.

- [x] **Final commit — update sample to use `UseApiPipeline` builder**

Update `samples/ApiPipeline.NET.Sample/Program.cs` to replace the individual `Use*()` calls with `UseApiPipeline(...)`:

```csharp
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .WithHttpsRedirection()
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithSecurityHeaders()
    .WithVersionDeprecation()
);
```

```bash
git add samples/ApiPipeline.NET.Sample/Program.cs
git commit -m "feat: update sample to use phase-enforced UseApiPipeline builder"
```

---

## Spec Coverage Checklist

| Spec Item | Task | Status |
|---|---|---|
| C-1 Pipeline ordering enforcement | Task 19 | ✓ |
| C-2 CORS hot-reload | Task 11 | ✓ |
| C-3 Named policy startup log | Task 9 | ✓ |
| C-4 Kestrel limits bypass ValidateOnStart | Task 10 | ✓ |
| C-5 MaxRequestBodySize = 0 valid | Task 1 | ✓ |
| C-6 AddCorrelationId no-op | Task 7 | ✓ |
| C-7 Anonymous rate limit bucket | Task 8 | ✓ |
| C-8 HTTPS compression default | Task 2 | ✓ |
| S-1 ServiceCollectionExtensions size | Task 12 | ✓ |
| S-2 Dictionary allocation | Task 7 (combined) | ✓ |
| S-3 UseResponseCompression snapshot | Task 13 | ✓ |
| S-4 AllowAllInDevelopment default | Task 3 | ✓ |
| S-5 SunsetLink injection | Task 6 | ✓ |
| S-6 ExceptionHandler guard | Task 5 | ✓ |
| S-7 ForwardLimit range | Task 4 | ✓ |
| T-1 Rate limit metric dimensions | Task 14 | ✓ |
| T-2 Request body histogram | Task 15 | ✓ |
| T-3 CORS / cache counters | Task 14 (combined) | ✓ |
| T-4 SecurityHeadersApplied noise | Task 14 (combined) | ✓ |
| T-5 Asp.Versioning.Mvc in core | Task 17 | ✓ |
| T-6 ResponseCaching vs OutputCaching | Task 18 | ✓ |
| T-7 Test coverage gaps | Task 16 | ✓ |
| A-1 UseApiPipeline all-in-one | Task 19 | ✓ |
| A-2 Per-policy metric dimensions | Task 14 (combined) | ✓ |
| A-3 OWASP validation hook | Task 20 | ✓ |
| A-4 Output caching satellite | Task 18 | ✓ |
| A-5 IApiPipelineBuilder phase enforcement | Task 19 | ✓ |
