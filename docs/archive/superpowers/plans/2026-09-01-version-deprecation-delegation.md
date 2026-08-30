# API Version Deprecation Delegation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete ApiPipeline.NET's custom API version deprecation subsystem and delegate the `Deprecation` / `Sunset` / `Link` headers to `Asp.Versioning` 10's built-in `DeprecationPolicy` and `SunsetPolicy`.

**Architecture:** Core (`ApiPipeline.NET`) loses all knowledge of API deprecation — no middleware, no options, no config key, no pipeline phase, no version-reader abstraction. The satellite package `ApiPipeline.NET.Versioning` becomes the whole feature: an options class bound from the existing `ApiVersionDeprecationOptions` config section, an `IConfigureOptions<ApiVersioningOptions>` that translates it into `Policies.Deprecate(...)` / `Policies.Sunset(...)` calls, an `IHostedService` that warns when version reporting is off, and an `IReportApiVersions` decorator that keeps the deprecation counter alive.

**Tech Stack:** .NET 10, ASP.NET Core, `Asp.Versioning.Http` 10.2.1, xUnit, FluentAssertions 7.1.0, `Microsoft.AspNetCore.TestHost`.

**Spec:** [docs/superpowers/specs/2026-09-01-version-deprecation-delegation-design.md](../specs/2026-09-01-version-deprecation-delegation-design.md)

## Global Constraints

- Target framework is `net10.0` for every project. Do not change it.
- `TreatWarningsAsErrors` is `true` and `GenerateDocumentationFile` is `true` repo-wide. **Every public type and public member — including in the test project — needs an XML doc comment, or the build fails with CS1591.** This bites immediately; write the `<summary>` as you write the member.
- Central Package Management is on. Never put a `Version` attribute on a `PackageReference`; add a `PackageVersion` to `Directory.Packages.props` instead.
- `ApiPipeline.NET` (core) must keep **zero** runtime NuGet dependencies. It must never reference `Asp.Versioning.*` or `ApiPipeline.NET.Versioning`.
- Target release is **2.0.0, Unreleased**. Straight removal, no `[Obsolete]` cycle.
- The config section name stays `ApiVersionDeprecationOptions`. Only the `PathPrefix` property is removed.
- Metric name `apipipeline.deprecation.headers_added` and meter name `ApiPipeline.NET` are unchanged.
- The test suite runs with `[assembly: CollectionBehavior(DisableTestParallelization = true)]` because telemetry instruments are process-global statics. Do not add parallelisation.
- `dotnet` is not on `PATH` in this environment. Use `/usr/local/share/dotnet/dotnet`.
- Full build+test command: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx -v minimal`
- Baseline before starting: **308 tests, all passing.**

---

## File Structure

**Created (all in `src/ApiPipeline.NET.Versioning/`):**

| File | Responsibility |
| --- | --- |
| `ApiVersionDeprecationOptions.cs` | The bound config section: `Enabled`, `DeprecatedVersions[]`, and `DeprecatedVersion`. Owns its own `IValidatableObject` rules. |
| `VersioningConfigurationKeys.cs` | The `ApiVersionDeprecationOptions` section-name constant, moved out of core. |
| `ConfigureDeprecationPolicies.cs` | `IConfigureOptions<ApiVersioningOptions>` translating options into framework policies. |
| `VersionReportingStartupWarning.cs` | `IHostedService` warning when the global `ReportApiVersions` is false. |
| `DeprecationReportingTelemetry.cs` | `IReportApiVersions` decorator incrementing the deprecation counter. |

**Modified:**

| File | Change |
| --- | --- |
| `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs` | Rewritten: binds options, registers the three new services, enforces registration order. |
| `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj` | `Asp.Versioning.Mvc` → `Asp.Versioning.Http`. |
| `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs:110-121` | `RecordDeprecationHeadersAdded(string? apiVersion)` → `(string? route)`, tag `api_version` → `http.route`. |
| `src/ApiPipeline.NET/Pipeline/ApiPipelineBuilder.cs` | Remove `Phase.VersionDeprecation`, `WithVersionDeprecation()`, its switch case, and the doc-comment references. |
| `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs` | Remove the `AddApiPipeline` branch, `AddApiPipelineVersionDeprecation`, `ConfigureApiVersionDeprecation`. |
| `src/ApiPipeline.NET/Extensions/ApplicationBuilderExtensions.cs:361-370` | Remove `UseApiVersionDeprecation()`. |
| `src/ApiPipeline.NET/Options/ApiPipelineServiceRegistrationOptions.cs:21` | Remove the `AddApiPipelineVersionDeprecation` flag. |
| `src/ApiPipeline.NET/Configuration/ApiPipelineConfigurationKeys.cs:38-41` | Remove the `ApiVersionDeprecation` constant. |
| `Directory.Packages.props` | Add `Asp.Versioning.Http` 10.2.1. |
| `samples/ApiPipeline.NET.Sample/ApiPipeline.NET.Sample.csproj` | Add explicit `Asp.Versioning.Mvc`. |
| `samples/ApiPipeline.NET.Sample/Program.cs` | Drop `.WithVersionDeprecation()`; add `ReportApiVersions = true`. |
| `tests/.../TestAppBuilder.cs:34,67` | Drop the registration and the config key. |
| `tests/.../ApiPipelineSmokeTests.cs:72,85,127` | Drop `UseApiVersionDeprecation()` and config keys. |
| `tests/.../PipelineContractTests.cs:94-175` | Delete the two short-circuit tests. |
| `tests/.../TelemetryCardinalityTests.cs` | Rewritten against the new route tag. |
| `tests/.../AspVersioningIntegrationTests.cs` | Rewritten end-to-end against the binder. |

**Deleted:**

- `src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs`
- `src/ApiPipeline.NET/Versioning/ApiVersionMatch.cs`
- `src/ApiPipeline.NET/Versioning/IApiPipelineVersionReader.cs`
- `src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs`
- `src/ApiPipeline.NET.Versioning/AspVersioningApiVersionReader.cs`
- `tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs`
- `tests/ApiPipeline.NET.Tests/ApiVersionMatchTests.cs`

---

## Task 1: Move the dependency to `Asp.Versioning.Http`

Do this first: every later task compiles against the new reference, and doing it last would mean discovering a missing type at the end.

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj`
- Modify: `samples/ApiPipeline.NET.Sample/ApiPipeline.NET.Sample.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `Asp.Versioning.Http` types available to `ApiPipeline.NET.Versioning` — `ApiVersioningOptions`, `IReportApiVersions`, `DefaultApiVersionReporter`; and transitively from `Asp.Versioning.Abstractions` — `ApiVersion`, `ApiVersionParser`, `IApiVersioningPolicyBuilder`, `SunsetPolicy`, `DeprecationPolicy`.

- [ ] **Step 1: Add the package version**

In `Directory.Packages.props`, add this line immediately after the `Asp.Versioning.Mvc` line, keeping alphabetical order:

```xml
    <PackageVersion Include="Asp.Versioning.Http" Version="10.2.1" />
```

Keep the existing `Asp.Versioning.Mvc` entry — the sample still needs it.

- [ ] **Step 2: Point the satellite at Http**

In `src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj`, replace:

```xml
    <PackageReference Include="Asp.Versioning.Mvc" />
```

with:

```xml
    <PackageReference Include="Asp.Versioning.Http" />
```

- [ ] **Step 3: Give the sample its own MVC reference**

The sample uses controllers and `.AddMvc()`, and until now received `Asp.Versioning.Mvc` transitively. In `samples/ApiPipeline.NET.Sample/ApiPipeline.NET.Sample.csproj`, add to the `ItemGroup` that already holds `CodeStyle.NET`:

```xml
    <PackageReference Include="Asp.Versioning.Mvc" />
```

- [ ] **Step 4: Verify the solution still builds and every test still passes**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx -v minimal`

Expected: `Passed!  - Failed: 0, Passed: 308`.

This proves the dependency swap alone changed no behaviour. If `AspVersioningApiVersionReader.cs` fails to compile, stop — it should not, because `IApiVersioningFeature` lives in `Asp.Versioning.Http`.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/ApiPipeline.NET.Versioning/ApiPipeline.NET.Versioning.csproj samples/ApiPipeline.NET.Sample/ApiPipeline.NET.Sample.csproj
git commit -m "build: drop ApiPipeline.NET.Versioning to Asp.Versioning.Http

The satellite referenced Asp.Versioning.Mvc only for the version reader.
Every type it needs is in Asp.Versioning.Http, so the lighter reference
makes the package usable from minimal-API apps with no MVC. The sample
declares its own Mvc reference, since it is the thing that chose
controllers."
```

---

## Task 2: The options class and its validation

**Files:**
- Create: `src/ApiPipeline.NET.Versioning/ApiVersionDeprecationOptions.cs`
- Create: `src/ApiPipeline.NET.Versioning/VersioningConfigurationKeys.cs`
- Test: `tests/ApiPipeline.NET.Tests/VersionDeprecationOptionsTests.cs`

**Interfaces:**
- Consumes: `Asp.Versioning.ApiVersionParser` (Task 1).
- Produces:
  - `ApiPipeline.NET.Versioning.VersioningConfigurationKeys.ApiVersionDeprecation` → `"ApiVersionDeprecationOptions"`
  - `ApiPipeline.NET.Versioning.ApiVersionDeprecationOptions` with `bool Enabled`, `DeprecatedVersion[] DeprecatedVersions`
  - `ApiPipeline.NET.Versioning.DeprecatedVersion` with `string Version`, `DateTimeOffset? DeprecationDate`, `DateTimeOffset? SunsetDate`, `string? SunsetLink`

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiPipeline.NET.Tests/VersionDeprecationOptionsTests.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using ApiPipeline.NET.Versioning;
using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Validation rules for the deprecation configuration section. Each rule replaces a failure that
/// was previously silent: an unparseable version simply never matched, and a sunset link with no
/// sunset date produced a link to nothing.
/// </summary>
public sealed class VersionDeprecationOptionsTests
{
    private static IList<ValidationResult> Validate(ApiVersionDeprecationOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    /// <summary>A version string the framework cannot parse must be rejected at startup.</summary>
    [Fact]
    public void UnparseableVersion_IsRejected()
    {
        var options = new ApiVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = "not-a-version",
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                },
            ],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("not-a-version");
    }

    /// <summary>
    /// RFC 9745 defines the Deprecation field value as a Structured Fields Date and provides no
    /// dateless form, so an entry without a date cannot produce a conformant header.
    /// </summary>
    [Fact]
    public void MissingDeprecationDate_IsRejected()
    {
        var options = new ApiVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions = [new DeprecatedVersion { Version = "1.0" }],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("DeprecationDate");
    }

    /// <summary>A sunset link with no sunset date advertises a date the response cannot carry.</summary>
    [Fact]
    public void SunsetLinkWithoutSunsetDate_IsRejected()
    {
        var options = new ApiVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = "1.0",
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                    SunsetLink = "https://docs.example.com/v1",
                },
            ],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("SunsetDate");
    }

    /// <summary>A relative or malformed link would produce a Link header no client can follow.</summary>
    [Fact]
    public void RelativeSunsetLink_IsRejected()
    {
        var options = new ApiVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = "1.0",
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                    SunsetDate = DateTimeOffset.UnixEpoch,
                    SunsetLink = "/docs/v1",
                },
            ],
        };

        Validate(options).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("absolute");
    }

    /// <summary>Nothing is validated when the feature is switched off.</summary>
    [Fact]
    public void Disabled_SkipsValidationEntirely()
    {
        var options = new ApiVersionDeprecationOptions
        {
            Enabled = false,
            DeprecatedVersions = [new DeprecatedVersion { Version = "not-a-version" }],
        };

        Validate(options).Should().BeEmpty();
    }

    /// <summary>Every spelling the framework accepts must pass.</summary>
    [Theory]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("2.1")]
    [InlineData("1.0-beta")]
    public void WellFormedConfiguration_Passes(string version)
    {
        var options = new ApiVersionDeprecationOptions
        {
            Enabled = true,
            DeprecatedVersions =
            [
                new DeprecatedVersion
                {
                    Version = version,
                    DeprecationDate = DateTimeOffset.UnixEpoch,
                    SunsetDate = DateTimeOffset.UnixEpoch.AddYears(1),
                    SunsetLink = "https://docs.example.com/v1",
                },
            ],
        };

        Validate(options).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~VersionDeprecationOptionsTests" -v minimal`

Expected: FAIL at compile time — `error CS0246: The type or namespace name 'ApiVersionDeprecationOptions' could not be found` in namespace `ApiPipeline.NET.Versioning`. (The old class is in `ApiPipeline.NET.Options`, a different namespace, and is not imported here.)

- [ ] **Step 3: Create the configuration key constant**

Create `src/ApiPipeline.NET.Versioning/VersioningConfigurationKeys.cs`:

```csharp
namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Configuration section keys consumed by <c>ApiPipeline.NET.Versioning</c>.
/// </summary>
/// <remarks>
/// Lives here rather than in <c>ApiPipeline.NET.Configuration.ApiPipelineConfigurationKeys</c>
/// because core no longer knows anything about API deprecation. The section name is unchanged from
/// 1.x, so existing <c>appsettings</c> files keep binding.
/// </remarks>
public static class VersioningConfigurationKeys
{
    /// <summary>The configuration section name for API version deprecation options.</summary>
    public const string ApiVersionDeprecation = "ApiVersionDeprecationOptions";
}
```

- [ ] **Step 4: Create the options class**

Create `src/ApiPipeline.NET.Versioning/ApiVersionDeprecationOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Asp.Versioning;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Which API versions are deprecated, and when. Bound from the <c>ApiVersionDeprecationOptions</c>
/// configuration section and translated into <c>Asp.Versioning</c> policies by
/// <see cref="ConfigureDeprecationPolicies"/>.
/// </summary>
/// <remarks>
/// The framework emits the resulting <c>Deprecation</c>, <c>Sunset</c> and <c>Link</c> headers; this
/// package only supplies the values, so that deprecation dates are operator-editable configuration
/// rather than a code change.
/// </remarks>
public sealed class ApiVersionDeprecationOptions : IValidatableObject
{
    /// <summary>
    /// Whether configured deprecation policies are applied. Defaults to <c>false</c>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The deprecated API versions and their dates.</summary>
    [MinLength(0)]
    public DeprecatedVersion[] DeprecatedVersions { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        foreach (var deprecated in DeprecatedVersions ?? [])
        {
            // Parsed with the framework's own parser, so a value that survives startup is a value
            // the routing layer will match. The custom matcher this replaces compared strings, so a
            // version it could not parse produced no error and no headers — only silence.
            if (!ApiVersionParser.Default.TryParse(deprecated.Version, out _))
            {
                yield return new ValidationResult(
                    $"DeprecatedVersions entry '{deprecated.Version}' is not a valid API version. " +
                    "Use the same spelling as the [ApiVersion] attribute, for example \"1.0\".",
                    [nameof(DeprecatedVersions)]);
            }

            if (deprecated.DeprecationDate is null)
            {
                yield return new ValidationResult(
                    $"DeprecatedVersions entry '{deprecated.Version}' requires a DeprecationDate. " +
                    "RFC 9745 defines the Deprecation header value as a Structured Fields Date and " +
                    "has no dateless form, so a version without a date cannot produce a conformant " +
                    "header.",
                    [nameof(DeprecatedVersions)]);
            }

            if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
            {
                if (!Uri.IsWellFormedUriString(deprecated.SunsetLink, UriKind.Absolute))
                {
                    yield return new ValidationResult(
                        $"DeprecatedVersions entry '{deprecated.Version}' has a SunsetLink that is " +
                        "not an absolute URI. Use a value such as " +
                        "'https://docs.example.com/v1-sunset'.",
                        [nameof(DeprecatedVersions)]);
                }

                if (deprecated.SunsetDate is null)
                {
                    yield return new ValidationResult(
                        $"DeprecatedVersions entry '{deprecated.Version}' has a SunsetLink but no " +
                        "SunsetDate. The link advertises a sunset the response cannot state — set " +
                        "SunsetDate, or remove SunsetLink.",
                        [nameof(DeprecatedVersions)]);
                }
            }
        }
    }
}

/// <summary>One deprecated API version and its dates.</summary>
public sealed class DeprecatedVersion
{
    /// <summary>
    /// The deprecated API version, spelled as in the <c>[ApiVersion]</c> attribute (for example
    /// <c>"1.0"</c>). Parsed at startup; an unparseable value fails the host.
    /// </summary>
    [Required, MinLength(1)]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// When the version became, or becomes, deprecated. Required — RFC 9745 provides no dateless
    /// form for the <c>Deprecation</c> field.
    /// </summary>
    [Required]
    public DateTimeOffset? DeprecationDate { get; set; }

    /// <summary>When the version will be switched off. Emitted as the <c>Sunset</c> header.</summary>
    public DateTimeOffset? SunsetDate { get; set; }

    /// <summary>
    /// An absolute URL documenting the deprecation. Emitted as a <c>Link</c> header.
    /// </summary>
    public string? SunsetLink { get; set; }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~VersionDeprecationOptionsTests" -v minimal`

Expected: `Passed! - Failed: 0, Passed: 9`.

There are now two `ApiVersionDeprecationOptions` types in the solution, in different namespaces. That is expected and temporary; Task 6 deletes the one in core.

- [ ] **Step 6: Commit**

```bash
git add src/ApiPipeline.NET.Versioning/ApiVersionDeprecationOptions.cs src/ApiPipeline.NET.Versioning/VersioningConfigurationKeys.cs tests/ApiPipeline.NET.Tests/VersionDeprecationOptionsTests.cs
git commit -m "feat(versioning): add deprecation options owned by the satellite package

Validation now parses each version with the framework's own
ApiVersionParser, so a value that survives startup is one the routing
layer will match. The matcher this replaces compared strings, so an
unparseable version produced no error and no headers."
```

---

## Task 3: Translate options into Asp.Versioning policies

**Files:**
- Create: `src/ApiPipeline.NET.Versioning/ConfigureDeprecationPolicies.cs`
- Modify: `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`
- Test: `tests/ApiPipeline.NET.Tests/AspVersioningIntegrationTests.cs` (rewritten in full)

**Interfaces:**
- Consumes: `ApiVersionDeprecationOptions`, `DeprecatedVersion`, `VersioningConfigurationKeys.ApiVersionDeprecation` (Task 2).
- Produces: `AddApiPipelineVersioning(this IServiceCollection services, IConfiguration configuration)` — signature unchanged from 1.x. Registers the options binding and `ConfigureDeprecationPolicies`.

- [ ] **Step 1: Replace the integration tests with end-to-end header assertions**

Overwrite `tests/ApiPipeline.NET.Tests/AspVersioningIntegrationTests.cs` entirely:

```csharp
using System.Globalization;
using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// End-to-end tests of the deprecation headers a caller actually receives.
/// <para>
/// These assert on the wire rather than on any type in this package: the headers are written by
/// <c>Asp.Versioning</c>'s reporter, and this package only supplies the policy values. If the
/// framework changes what it emits, these fail — which is the point.
/// </para>
/// </summary>
public sealed class AspVersioningIntegrationTests
{
    private static readonly DateTimeOffset DeprecatedOn =
        new(2025, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SunsetOn =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, string?> DeprecationConfig(bool enabled = true) => new()
    {
        ["ApiVersionDeprecationOptions:Enabled"] = enabled ? "true" : "false",
        ["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0",
        ["ApiVersionDeprecationOptions:DeprecatedVersions:0:DeprecationDate"] = "2025-07-01T00:00:00Z",
        ["ApiVersionDeprecationOptions:DeprecatedVersions:0:SunsetDate"] = "2026-01-01T00:00:00Z",
        ["ApiVersionDeprecationOptions:DeprecatedVersions:0:SunsetLink"] = "https://docs.example.com/v1-sunset",
    };

    private static WebApplication CreateApp(
        Dictionary<string, string?> config,
        bool reportApiVersions = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddRouting();
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = reportApiVersions;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });
        builder.Services.AddApiPipelineVersioning(builder.Configuration);
        return builder.Build();
    }

    private static void MapVersionedEndpoints(WebApplication app)
    {
        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .HasApiVersion(new ApiVersion(2, 0))
            .Build();

        app.MapGet("/api/v{version:apiVersion}/orders", () => Results.Ok("v1"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));

        app.MapGet("/api/v{version:apiVersion}/orders-v2", () => Results.Ok("v2"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(2, 0));
    }

    /// <summary>
    /// RFC 9745 §2 defines the field value as a Structured Fields Date: "@" followed by the
    /// deprecation instant as a Unix timestamp. The superseded draft spellings ("true", an
    /// HTTP-date) are not conformant. Pinned here so a framework change is caught rather than
    /// shipped.
    /// </summary>
    [Fact]
    public async Task Deprecation_HeaderValue_IsRfc9745StructuredFieldDate()
    {
        await using var app = CreateApp(DeprecationConfig());
        MapVersionedEndpoints(app);
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/orders");

        var expected = "@" + DeprecatedOn.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        response.Headers.GetValues("Deprecation").Single().Should().Be(expected);
    }

    /// <summary>All three headers reach a caller of a deprecated version.</summary>
    [Fact]
    public async Task DeprecatedVersion_Request_EmitsDeprecationSunsetAndLink()
    {
        await using var app = CreateApp(DeprecationConfig());
        MapVersionedEndpoints(app);
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/orders");

        response.Headers.Contains("Deprecation").Should().BeTrue();
        response.Headers.GetValues("Sunset").Single()
            .Should().Be(SunsetOn.ToString("R", CultureInfo.InvariantCulture));
        string.Join(" ", response.Headers.GetValues("Link"))
            .Should().Contain("https://docs.example.com/v1-sunset")
            .And.Contain("rel=\"sunset\"");
    }

    /// <summary>A caller on a supported version must not be warned.</summary>
    [Fact]
    public async Task NonDeprecatedVersion_Request_EmitsNoDeprecationHeaders()
    {
        await using var app = CreateApp(DeprecationConfig());
        MapVersionedEndpoints(app);
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v2/orders-v2");

        response.Headers.Contains("Deprecation").Should().BeFalse();
        response.Headers.Contains("Sunset").Should().BeFalse();
    }

    /// <summary>The off switch is honoured: no policies are registered, so nothing is emitted.</summary>
    [Fact]
    public async Task Disabled_EmitsNothing()
    {
        await using var app = CreateApp(DeprecationConfig(enabled: false));
        MapVersionedEndpoints(app);
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/orders");

        response.Headers.Contains("Deprecation").Should().BeFalse();
        response.Headers.Contains("Sunset").Should().BeFalse();
    }

    /// <summary>
    /// Version matching is the framework's, so every spelling the routing layer accepts for a
    /// version resolves to the same configured policy. This is the invariant the deleted
    /// ApiVersionMatch helper used to hold by hand.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/orders")]
    [InlineData("/api/v1.0/orders")]
    public async Task VersionSpellingVariants_AllMatch(string path)
    {
        await using var app = CreateApp(DeprecationConfig());
        MapVersionedEndpoints(app);
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync(path);

        response.Headers.Contains("Deprecation").Should()
            .BeTrue($"'{path}' addresses the configured deprecated version 1.0");
    }

    /// <summary>
    /// An unparseable configured version fails the host instead of silently never matching, which
    /// is what the string-comparing matcher this replaces did.
    /// </summary>
    [Fact]
    public async Task UnparseableVersion_FailsStartup()
    {
        var config = DeprecationConfig();
        config["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "not-a-version";

        await using var app = CreateApp(config);
        MapVersionedEndpoints(app);

        var act = async () => await app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .Where(ex => ex.Message.Contains("not-a-version"));
    }

    /// <summary>
    /// Accepted behaviour change: Asp.Versioning reports versions only on responses that reach the
    /// selected endpoint, so a short-circuited response carries no deprecation headers. The
    /// middleware this replaces registered an OnStarting callback early in the pipeline and did.
    /// Pinned so a future change to this is deliberate.
    /// </summary>
    [Fact]
    public async Task Deprecation_HeadersAbsent_OnShortCircuitedResponse()
    {
        await using var app = CreateApp(DeprecationConfig());

        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .Build();

        // Short-circuits before the endpoint runs, standing in for a 401 or a 429.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Short-Circuit"))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            await next(context);
        });

        app.MapGet("/api/v{version:apiVersion}/orders", () => Results.Ok("v1"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));

        await app.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders");
        request.Headers.Add("X-Short-Circuit", "1");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.Contains("Deprecation").Should()
            .BeFalse("Asp.Versioning reports versions only on responses that reach the endpoint");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~AspVersioningIntegrationTests" -v minimal`

Expected: FAIL. The headers are absent, because `AddApiPipelineVersioning` still registers only the old reader and no policies. `Deprecation_HeaderValue_IsRfc9745StructuredFieldDate` fails with `InvalidOperationException` on `GetValues("Deprecation")`.

- [ ] **Step 3: Write the policy binder**

Create `src/ApiPipeline.NET.Versioning/ConfigureDeprecationPolicies.cs`:

```csharp
using Asp.Versioning;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Applies <see cref="ApiVersionDeprecationOptions"/> to <c>Asp.Versioning</c>'s own policy builder,
/// so deprecation dates come from configuration rather than from code.
/// </summary>
/// <remarks>
/// Registered as an <see cref="IConfigureOptions{TOptions}"/> so it composes onto whatever the
/// consuming application configured in its own <c>AddApiVersioning(...)</c> call instead of
/// replacing it. Once these policies are registered, the framework's
/// <c>DefaultApiVersionReporter</c> writes the <c>Deprecation</c>, <c>Sunset</c> and <c>Link</c>
/// headers; this package writes no headers itself.
/// </remarks>
internal sealed class ConfigureDeprecationPolicies : IConfigureOptions<ApiVersioningOptions>
{
    private readonly IOptions<ApiVersionDeprecationOptions> _deprecation;

    public ConfigureDeprecationPolicies(IOptions<ApiVersionDeprecationOptions> deprecation)
        => _deprecation = deprecation;

    public void Configure(ApiVersioningOptions options)
    {
        var settings = _deprecation.Value;
        if (!settings.Enabled)
        {
            return;
        }

        foreach (var deprecated in settings.DeprecatedVersions ?? [])
        {
            // Validation has already run, so this parses. TryParse rather than Parse so a
            // configuration source that somehow bypassed validation degrades to skipping one entry
            // instead of taking down the host from inside an options callback.
            if (!ApiVersionParser.Default.TryParse(deprecated.Version, out var apiVersion)
                || apiVersion is null)
            {
                continue;
            }

            var deprecation = options.Policies.Deprecate(apiVersion);
            if (deprecated.DeprecationDate is { } deprecationDate)
            {
                deprecation.Effective(deprecationDate);
            }

            if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
            {
                deprecation.Link(deprecated.SunsetLink).Title("deprecation").Type("text/html");
            }

            // Sunset is a separate policy: a version can be deprecated long before a switch-off
            // date exists, and RFC 8594's Sunset header is only meaningful once one does.
            if (deprecated.SunsetDate is { } sunsetDate)
            {
                var sunset = options.Policies.Sunset(apiVersion).Effective(sunsetDate);
                if (!string.IsNullOrWhiteSpace(deprecated.SunsetLink))
                {
                    sunset.Link(deprecated.SunsetLink).Title("sunset").Type("text/html");
                }
            }
        }
    }
}
```

- [ ] **Step 4: Rewrite the registration extension**

Overwrite `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`:

```csharp
using Asp.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Registers configuration-driven API version deprecation on top of <c>Asp.Versioning</c>.
/// </summary>
public static class VersioningServiceCollectionExtensions
{
    /// <summary>
    /// Binds the <c>ApiVersionDeprecationOptions</c> configuration section and applies it to
    /// <c>Asp.Versioning</c>'s deprecation and sunset policies, so the framework emits the
    /// <c>Deprecation</c>, <c>Sunset</c> and <c>Link</c> headers.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">Configuration containing the <c>ApiVersionDeprecationOptions</c> section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance to enable fluent chaining.</returns>
    /// <remarks>
    /// Call this <em>after</em> <c>AddApiVersioning(...)</c>. The reader, default version and
    /// reporting choices stay with the consuming application; this method only supplies policy
    /// values from configuration.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddApiVersioning(o => o.ReportApiVersions = true);
    /// builder.Services.AddApiPipelineVersioning(builder.Configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPipelineVersioning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ApiVersionDeprecationOptions>()
            .Bind(configuration.GetSection(VersioningConfigurationKeys.ApiVersionDeprecation))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<ApiVersioningOptions>, ConfigureDeprecationPolicies>());

        return services;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~AspVersioningIntegrationTests" -v minimal`

Expected: `Passed! - Failed: 0, Passed: 8`.

If `Deprecation_HeaderValue_IsRfc9745StructuredFieldDate` fails, the framework's format has changed since this plan was written — stop and report the actual value rather than adjusting the expectation.

- [ ] **Step 6: Commit**

```bash
git add src/ApiPipeline.NET.Versioning/ConfigureDeprecationPolicies.cs src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs tests/ApiPipeline.NET.Tests/AspVersioningIntegrationTests.cs
git commit -m "feat(versioning): bind deprecation config to Asp.Versioning policies

Registration composes onto the application's own AddApiVersioning call
through IConfigureOptions instead of replacing it. Tests now assert the
headers on the wire rather than the internals of a middleware, and pin
the RFC 9745 Deprecation format plus the accepted loss of headers on
short-circuited responses."
```

---

## Task 4: Warn when version reporting is off

**Files:**
- Create: `src/ApiPipeline.NET.Versioning/VersionReportingStartupWarning.cs`
- Modify: `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`
- Test: `tests/ApiPipeline.NET.Tests/VersionReportingWarningTests.cs`

**Interfaces:**
- Consumes: `ApiVersionDeprecationOptions` (Task 2), `AddApiPipelineVersioning` (Task 3).
- Produces: no new public API. Registers an additional `IHostedService`.

**Why a warning and not a throw:** version reporting is enabled by **either** the global `ApiVersioningOptions.ReportApiVersions` **or** a per-endpoint `NewApiVersionSet().ReportApiVersions()`. Throwing on the global flag alone would fail startup for an application that enables it per version set, which works correctly. Proving the negative would mean walking `EndpointDataSource` and reading `Asp.Versioning` endpoint metadata — the framework coupling this whole change removes.

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiPipeline.NET.Tests/VersionReportingWarningTests.cs`:

```csharp
using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Deprecation policies with version reporting switched off emit nothing. That is a silent failure,
/// so it is logged — but not fatal, because reporting can also be enabled per version set, which is
/// not observable from the global options.
/// </summary>
public sealed class VersionReportingWarningTests
{
    private static WebApplication CreateApp(
        bool reportApiVersions,
        bool deprecationEnabled,
        CapturingLoggerProvider logs)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiVersionDeprecationOptions:Enabled"] = deprecationEnabled ? "true" : "false",
            ["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0",
            ["ApiVersionDeprecationOptions:DeprecatedVersions:0:DeprecationDate"] = "2025-07-01T00:00:00Z",
        });
        builder.Services.AddRouting();
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = reportApiVersions;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });
        builder.Services.AddApiPipelineVersioning(builder.Configuration);
        return builder.Build();
    }

    /// <summary>Policies configured with reporting off must produce a warning, not silence.</summary>
    [Fact]
    public async Task ReportApiVersionsFalse_WithPolicies_LogsWarning()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = CreateApp(reportApiVersions: false, deprecationEnabled: true, logs);

        await app.StartAsync();

        string.Join("\n", logs.Messages).Should().Contain("ReportApiVersions");
    }

    /// <summary>Startup must still succeed — reporting may be enabled per version set.</summary>
    [Fact]
    public async Task ReportApiVersionsFalse_WithPolicies_DoesNotFailStartup()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = CreateApp(reportApiVersions: false, deprecationEnabled: true, logs);

        var act = async () => await app.StartAsync();

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// The configuration the warning must never become a failure for: reporting off globally, on
    /// for the endpoint's version set. Headers are emitted, so failing here would break a working
    /// application.
    /// </summary>
    [Fact]
    public async Task ReportApiVersionsFalse_PerEndpointSetTrue_StillEmitsHeaders()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = CreateApp(reportApiVersions: false, deprecationEnabled: true, logs);

        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        app.MapGet("/api/v{version:apiVersion}/orders", () => Results.Ok("v1"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));

        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/orders");

        response.Headers.Contains("Deprecation").Should()
            .BeTrue("a version set may enable reporting independently of the global flag");
    }

    /// <summary>No policies configured means nothing to warn about.</summary>
    [Fact]
    public async Task ReportApiVersionsFalse_WithoutPolicies_LogsNothing()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = CreateApp(reportApiVersions: false, deprecationEnabled: false, logs);

        await app.StartAsync();

        string.Join("\n", logs.Messages).Should().NotContain("ReportApiVersions");
    }
}
```

`CapturingLoggerProvider` already exists at `tests/ApiPipeline.NET.Tests/CapturingLoggerProvider.cs` with a `List<string> Messages` property. Use it as-is; do not create another.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~VersionReportingWarningTests" -v minimal`

Expected: `ReportApiVersionsFalse_WithPolicies_LogsWarning` FAILS (no warning is logged). The other three pass already.

- [ ] **Step 3: Write the hosted service**

Create `src/ApiPipeline.NET.Versioning/VersionReportingStartupWarning.cs`:

```csharp
using Asp.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Warns at startup when deprecation policies are configured but API version reporting is switched
/// off globally, in which case those policies may produce no headers at all.
/// </summary>
/// <remarks>
/// A warning rather than a startup failure, deliberately. Version reporting is enabled by
/// <em>either</em> <see cref="ApiVersioningOptions.ReportApiVersions"/> or a per-endpoint
/// <c>NewApiVersionSet().ReportApiVersions()</c>, and the second is not observable from options.
/// Throwing on the global flag alone would fail a correctly configured application, which is worse
/// than the silence being reported.
/// </remarks>
internal sealed class VersionReportingStartupWarning : IHostedService
{
    private readonly IOptions<ApiVersionDeprecationOptions> _deprecation;
    private readonly IOptions<ApiVersioningOptions> _versioning;
    private readonly ILogger<VersionReportingStartupWarning> _logger;

    public VersionReportingStartupWarning(
        IOptions<ApiVersionDeprecationOptions> deprecation,
        IOptions<ApiVersioningOptions> versioning,
        ILogger<VersionReportingStartupWarning> logger)
    {
        _deprecation = deprecation;
        _versioning = versioning;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _deprecation.Value;
        var hasPolicies = settings.Enabled && (settings.DeprecatedVersions ?? []).Length > 0;

        if (hasPolicies && !_versioning.Value.ReportApiVersions)
        {
            _logger.LogWarning(
                "API version deprecation policies are configured, but ApiVersioningOptions." +
                "ReportApiVersions is false. Deprecation, Sunset and Link headers are emitted only " +
                "for endpoints whose version set calls ReportApiVersions(); every other endpoint " +
                "will emit nothing. Set ReportApiVersions = true in AddApiVersioning(...) to apply " +
                "it everywhere, call ReportApiVersions() on the version sets that need it, or set " +
                "ApiVersionDeprecationOptions:Enabled to false.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: Register it**

In `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`, add immediately after the `ConfigureDeprecationPolicies` registration and before `return services;`:

```csharp
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, VersionReportingStartupWarning>());
```

Add `using Microsoft.Extensions.Hosting;` to the file's usings.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~VersionReportingWarningTests" -v minimal`

Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 6: Commit**

```bash
git add src/ApiPipeline.NET.Versioning/VersionReportingStartupWarning.cs src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs tests/ApiPipeline.NET.Tests/VersionReportingWarningTests.cs
git commit -m "feat(versioning): warn when deprecation policies cannot be reported

Warns rather than throws: reporting is enabled by either the global flag
or a per-endpoint version set, and the second is not visible from
options, so failing on the global flag alone would take down an
application that works."
```

---

## Task 5: Keep the deprecation counter, tagged by route

**Files:**
- Create: `src/ApiPipeline.NET.Versioning/DeprecationReportingTelemetry.cs`
- Modify: `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`
- Modify: `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs:104-121`
- Test: `tests/ApiPipeline.NET.Tests/TelemetryCardinalityTests.cs` (rewritten in full)

**Interfaces:**
- Consumes: `AddApiPipelineVersioning` (Task 3).
- Produces: `ApiPipelineTelemetry.RecordDeprecationHeadersAdded(string? route = null)` — signature changed from `(string? apiVersion = null)`; tag key changes from `api_version` to `http.route`.

- [ ] **Step 1: Rewrite the telemetry test**

Overwrite `tests/ApiPipeline.NET.Tests/TelemetryCardinalityTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Cardinality contract for the deprecation counter. Metric dimensions must come from a bounded
/// value space: a caller-controlled string would let one client mint unlimited series, each a
/// permanent allocation in the SDK and in the metrics backend.
/// </summary>
public sealed class TelemetryCardinalityTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Dictionary<string, object?>> _measurements = [];

    /// <summary>Starts a listener capturing the deprecation counter.</summary>
    public TelemetryCardinalityTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "ApiPipeline.NET"
                && instrument.Name == "apipipeline.deprecation.headers_added")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var captured = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                captured[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add(captured);
            }
        });

        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiVersionDeprecationOptions:Enabled"] = "true",
            ["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0",
            ["ApiVersionDeprecationOptions:DeprecatedVersions:0:DeprecationDate"] = "2025-07-01T00:00:00Z",
        });
        builder.Services.AddRouting();
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });
        builder.Services.AddApiPipelineVersioning(builder.Configuration);
        return builder.Build();
    }

    private static void MapDeprecatedEndpoint(WebApplication app)
    {
        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .Build();

        app.MapGet("/api/v{version:apiVersion}/orders/{id}", (string id) => Results.Ok(id))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));
    }

    /// <summary>
    /// The route template, never the raw path. Templates are bounded by the number of endpoints;
    /// paths are not, so tagging "/api/v1/orders/12345" would create one series per order id.
    /// </summary>
    [Fact]
    public async Task DeprecationTelemetry_TagsRouteTemplateOnly()
    {
        await using var app = CreateApp();
        MapDeprecatedEndpoint(app);
        await app.StartAsync();

        await app.GetTestClient().GetAsync("/api/v1/orders/12345");

        _measurements.Should().ContainSingle();
        var tags = _measurements[0];
        tags.Should().ContainKey("http.route");
        tags["http.route"].Should().Be("/api/v{version:apiVersion}/orders/{id}");
        tags.Should().NotContainKey("api_version");
    }

    /// <summary>Two calls to the same endpoint share one series.</summary>
    [Fact]
    public async Task DeprecationTelemetry_RepeatedCalls_ShareOneSeries()
    {
        await using var app = CreateApp();
        MapDeprecatedEndpoint(app);
        await app.StartAsync();

        var client = app.GetTestClient();
        await client.GetAsync("/api/v1/orders/1");
        await client.GetAsync("/api/v1/orders/2");

        _measurements.Should().HaveCount(2);
        _measurements.Select(m => m["http.route"]).Distinct().Should().ContainSingle();
    }

    /// <summary>
    /// The decorator must never suppress the headers it observes. Telemetry is an addition to the
    /// framework's reporting, not a replacement for it.
    /// </summary>
    [Fact]
    public async Task DeprecationTelemetry_DoesNotSuppressHeaders()
    {
        await using var app = CreateApp();
        MapDeprecatedEndpoint(app);
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/orders/1");

        response.Headers.Contains("Deprecation").Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~TelemetryCardinalityTests" -v minimal`

Expected: `DeprecationTelemetry_TagsRouteTemplateOnly` and `..._RepeatedCalls_ShareOneSeries` FAIL — `_measurements` is empty, because nothing records the counter any more once the old middleware is out of the picture. `..._DoesNotSuppressHeaders` passes already.

- [ ] **Step 3: Change the telemetry method's dimension**

In `src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs`, replace the whole `RecordDeprecationHeadersAdded` method and its doc comment (lines 104-121) with:

```csharp
    /// <summary>Records that deprecation headers were added to a response.</summary>
    /// <param name="route">
    /// The matched route <em>template</em> (for example <c>/orders/{id}</c>), never the raw path.
    /// Templates are bounded by the number of endpoints; raw paths are not, and would let one
    /// caller mint unlimited metric series.
    /// </param>
    public static void RecordDeprecationHeadersAdded(string? route = null)
    {
        if (route is { Length: > 0 })
        {
            DeprecationHeadersAddedCount.Add(1,
                new KeyValuePair<string, object?>("http.route", route));
        }
        else
        {
            DeprecationHeadersAddedCount.Add(1);
        }
    }
```

- [ ] **Step 4: Write the reporter decorator**

Create `src/ApiPipeline.NET.Versioning/DeprecationReportingTelemetry.cs`:

```csharp
using ApiPipeline.NET.Observability;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Wraps the application's <see cref="IReportApiVersions"/> and records
/// <c>apipipeline.deprecation.headers_added</c> whenever the inner reporter wrote a
/// <c>Deprecation</c> header.
/// </summary>
/// <remarks>
/// A decorator rather than middleware: core no longer has a pipeline phase to host one, and a
/// <c>Use*</c> call the consumer had to remember would be exactly the silent-failure wiring this
/// package exists to remove. It also costs nothing on requests that report no versions.
/// <para>
/// The inner reporter is always invoked first and unconditionally, so telemetry can never suppress
/// a header.
/// </para>
/// </remarks>
internal sealed class DeprecationReportingTelemetry : IReportApiVersions
{
    private readonly IReportApiVersions _inner;

    public DeprecationReportingTelemetry(IReportApiVersions inner) => _inner = inner;

    /// <inheritdoc />
    public ApiVersionMapping Mapping => _inner.Mapping;

    /// <inheritdoc />
    public void Report(HttpResponse response, ApiVersionModel apiVersionModel)
    {
        _inner.Report(response, apiVersionModel);

        if (!response.Headers.ContainsKey("Deprecation"))
        {
            return;
        }

        // The template, not the path: bounded by the number of endpoints.
        var route = (response.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        ApiPipelineTelemetry.RecordDeprecationHeadersAdded(route);
    }
}
```

- [ ] **Step 5: Register the decorator**

In `src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs`, add this immediately before `return services;`:

```csharp
        // Wraps whatever reporter the application has, so a consumer-supplied IReportApiVersions
        // keeps working. AddApiVersioning must therefore have run already — hence the check.
        var reporter = services.LastOrDefault(d => d.ServiceType == typeof(IReportApiVersions))
            ?? throw new InvalidOperationException(
                "AddApiPipelineVersioning(...) must be called after AddApiVersioning(...): no " +
                "IReportApiVersions is registered yet. Move the AddApiVersioning(...) call above " +
                "this one.");

        services.Remove(reporter);
        services.Add(ServiceDescriptor.Describe(
            typeof(IReportApiVersions),
            provider => new DeprecationReportingTelemetry(
                (IReportApiVersions)CreateInstance(provider, reporter)),
            reporter.Lifetime));
```

and add this private helper method to the same class, after `AddApiPipelineVersioning`:

```csharp
    /// <summary>
    /// Materialises the descriptor that was registered before decoration, whichever of the three
    /// forms it used.
    /// </summary>
    private static object CreateInstance(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }

        return ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }
```

Add `using Asp.Versioning;` if it is not already present.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~TelemetryCardinalityTests" -v minimal`

Expected: `Passed! - Failed: 0, Passed: 3`.

- [ ] **Step 7: Add the ordering-contract and custom-reporter tests**

Append these two tests to `tests/ApiPipeline.NET.Tests/VersionReportingWarningTests.cs`, inside the class:

```csharp
    /// <summary>
    /// The decorator wraps a descriptor that AddApiVersioning registers, so the wrong order has to
    /// fail loudly at registration rather than silently dropping telemetry.
    /// </summary>
    [Fact]
    public void AddApiPipelineVersioning_BeforeAddApiVersioning_Throws()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var act = () => services.AddApiPipelineVersioning(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be called after AddApiVersioning*");
    }

    /// <summary>Decoration preserves a reporter the consuming application supplied itself.</summary>
    [Fact]
    public async Task CustomReportApiVersions_IsStillInvoked()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiVersionDeprecationOptions:Enabled"] = "true",
            ["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0",
            ["ApiVersionDeprecationOptions:DeprecatedVersions:0:DeprecationDate"] = "2025-07-01T00:00:00Z",
        });
        builder.Services.AddRouting();
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });
        builder.Services.AddSingleton<IReportApiVersions, MarkerReporter>();
        builder.Services.AddApiPipelineVersioning(builder.Configuration);

        await using var app = builder.Build();
        var versionSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(new ApiVersion(1, 0))
            .Build();
        app.MapGet("/api/v{version:apiVersion}/orders", () => Results.Ok("v1"))
           .WithApiVersionSet(versionSet)
           .MapToApiVersion(new ApiVersion(1, 0));

        await app.StartAsync();
        var response = await app.GetTestClient().GetAsync("/api/v1/orders");

        response.Headers.Contains("X-Marker").Should()
            .BeTrue("the application's own reporter must still run under the decorator");
    }

    /// <summary>A reporter that stamps a marker header, standing in for a consumer's own.</summary>
    private sealed class MarkerReporter : IReportApiVersions
    {
        public ApiVersionMapping Mapping => ApiVersionMapping.Explicit | ApiVersionMapping.Implicit;

        public void Report(HttpResponse response, ApiVersionModel apiVersionModel)
            => response.Headers["X-Marker"] = "1";
    }
```

Add these usings to the top of that file:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx --filter "FullyQualifiedName~VersionReportingWarningTests" -v minimal`

Expected: `Passed! - Failed: 0, Passed: 6`.

If `CustomReportApiVersions_IsStillInvoked` fails because the marker header is absent, the decorator is wrapping the wrong descriptor — check that `LastOrDefault` picks the consumer's registration and not the framework's.

- [ ] **Step 9: Commit**

```bash
git add src/ApiPipeline.NET.Versioning/DeprecationReportingTelemetry.cs src/ApiPipeline.NET.Versioning/VersioningServiceCollectionExtensions.cs src/ApiPipeline.NET/Observability/ApiPipelineTelemetry.cs tests/ApiPipeline.NET.Tests/TelemetryCardinalityTests.cs tests/ApiPipeline.NET.Tests/VersionReportingWarningTests.cs
git commit -m "feat(versioning): keep the deprecation counter via a reporter decorator

BREAKING: RecordDeprecationHeadersAdded takes a route rather than an api
version, and the tag changes from api_version to http.route. The metric
and meter names are unchanged.

The decorator needs no pipeline wiring and delegates to the inner
reporter unconditionally, so telemetry can never suppress a header."
```

---

## Task 6: Delete the subsystem from core

Everything the new implementation needs now exists and is tested. This task removes the old one.

**Files:**
- Delete: `src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs`
- Delete: `src/ApiPipeline.NET/Versioning/ApiVersionMatch.cs`
- Delete: `src/ApiPipeline.NET/Versioning/IApiPipelineVersionReader.cs`
- Delete: `src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs`
- Delete: `src/ApiPipeline.NET.Versioning/AspVersioningApiVersionReader.cs`
- Delete: `tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs`
- Delete: `tests/ApiPipeline.NET.Tests/ApiVersionMatchTests.cs`
- Modify: `src/ApiPipeline.NET/Pipeline/ApiPipelineBuilder.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/ApiPipeline.NET/Extensions/ApplicationBuilderExtensions.cs`
- Modify: `src/ApiPipeline.NET/Options/ApiPipelineServiceRegistrationOptions.cs`
- Modify: `src/ApiPipeline.NET/Configuration/ApiPipelineConfigurationKeys.cs`
- Modify: `tests/ApiPipeline.NET.Tests/TestAppBuilder.cs`
- Modify: `tests/ApiPipeline.NET.Tests/ApiPipelineSmokeTests.cs`
- Modify: `tests/ApiPipeline.NET.Tests/PipelineContractTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2-5.
- Produces: core with no deprecation surface. `ApiPipelineBuilder` phases become: `ForwardedHeaders, RequestSizeTracking, CorrelationId, SecurityHeaders, RequestTimeouts, HttpsRedirection, Cors, EdgeRateLimiting, Authentication, Authorization, RateLimiting, ResponseCompression, ResponseCaching, OutputCaching`.

- [ ] **Step 1: Delete the files**

```bash
git rm src/ApiPipeline.NET/Middleware/ApiVersionDeprecationMiddleware.cs \
       src/ApiPipeline.NET/Versioning/ApiVersionMatch.cs \
       src/ApiPipeline.NET/Versioning/IApiPipelineVersionReader.cs \
       src/ApiPipeline.NET/Options/ApiVersionDeprecationOptions.cs \
       src/ApiPipeline.NET.Versioning/AspVersioningApiVersionReader.cs \
       tests/ApiPipeline.NET.Tests/ApiVersionDeprecationMiddlewareTests.cs \
       tests/ApiPipeline.NET.Tests/ApiVersionMatchTests.cs
```

The `src/ApiPipeline.NET/Versioning/` directory is now empty; remove it if git leaves it behind.

- [ ] **Step 2: Strip the pipeline phase**

In `src/ApiPipeline.NET/Pipeline/ApiPipelineBuilder.cs`:

Remove the enum member on line 54:

```csharp
        VersionDeprecation,
```

Remove the `WithVersionDeprecation` method and its doc comment (around line 100-105):

```csharp
    /// <summary>
    /// Adds API version deprecation headers (Headers phase). Ordered alongside
    /// <see cref="WithSecurityHeaders"/> so a deprecated-version caller is warned even when the
    /// response is a 429, a 401, or a cache hit.
    /// </summary>
    public ApiPipelineBuilder WithVersionDeprecation() => With(Phase.VersionDeprecation);
```

Remove the switch case on line 177:

```csharp
                case Phase.VersionDeprecation:  app.UseApiVersionDeprecation(); break;
```

In the `Phase` enum's doc comment, replace the second invariant bullet so it no longer names a phase that does not exist:

```csharp
    ///  * SecurityHeaders MUST precede every phase that can short-circuit the pipeline — CORS
    ///    (preflight 204), RequestTimeouts (504), Authorization (401/403), RateLimiting (429), the
    ///    caches (cache hit), and anything the application short-circuits with downstream. The
    ///    middleware only registers its Response.OnStarting callback when the request reaches it,
    ///    so anything ordered before it loses the headers entirely. Running it early costs nothing:
    ///    the callback still fires at response start, and every header write is guarded by a
    ///    ContainsKey check so later middleware can still override.
```

- [ ] **Step 3: Strip the service registrations**

In `src/ApiPipeline.NET/Extensions/ServiceCollectionExtensions.cs`:

Remove the branch in `AddApiPipeline` (lines 66-69):

```csharp
        if (options.AddApiPipelineVersionDeprecation)
        {
            services.AddApiPipelineVersionDeprecation(configuration);
        }
```

Remove the whole `AddApiPipelineVersionDeprecation` method with its doc comment (around lines 272-282), and the whole `ConfigureApiVersionDeprecation` method with its doc comment and both `.Validate(...)` rules (around lines 481-518).

Remove the now-unused `using` for the deleted options type if the compiler flags it.

- [ ] **Step 4: Strip the middleware extension**

In `src/ApiPipeline.NET/Extensions/ApplicationBuilderExtensions.cs`, remove `UseApiVersionDeprecation` and its doc comment (lines 361-370):

```csharp
    /// <summary>
    /// Adds the API version deprecation middleware to the HTTP pipeline.
    /// </summary>
    /// <param name="app">The application pipeline to configure.</param>
    /// <returns>The same <see cref="IApplicationBuilder"/> instance for chaining.</returns>
    public static IApplicationBuilder UseApiVersionDeprecation(this IApplicationBuilder app)
    {
        app.UseMiddleware<ApiVersionDeprecationMiddleware>();
        return app;
    }
```

- [ ] **Step 5: Strip the registration flag and the config key**

In `src/ApiPipeline.NET/Options/ApiPipelineServiceRegistrationOptions.cs`, remove line 21 and its doc comment:

```csharp
    /// <summary>Registers API version deprecation settings.</summary>
    public bool AddApiPipelineVersionDeprecation { get; set; } = true;
```

In `src/ApiPipeline.NET/Configuration/ApiPipelineConfigurationKeys.cs`, remove lines 38-41:

```csharp
    /// <summary>
    /// The configuration section name for API version deprecation options.
    /// </summary>
    public const string ApiVersionDeprecation = "ApiVersionDeprecationOptions";
```

- [ ] **Step 6: Update the test harness**

In `tests/ApiPipeline.NET.Tests/TestAppBuilder.cs`, remove line 34:

```csharp
            .AddApiPipelineVersionDeprecation(builder.Configuration)
```

and remove line 67 from `MinimalConfig`:

```csharp
            ["ApiVersionDeprecationOptions:Enabled"] = "false",
```

In `tests/ApiPipeline.NET.Tests/ApiPipelineSmokeTests.cs`, remove the `["ApiVersionDeprecationOptions:Enabled"] = "false"` entries on lines 72 and 127, and remove `app.UseApiVersionDeprecation();` on line 85.

- [ ] **Step 7: Delete the two short-circuit tests**

In `tests/ApiPipeline.NET.Tests/PipelineContractTests.cs`, delete `VersionDeprecation_HeadersPresent_On429` (starting around line 94) and `VersionDeprecation_HeadersPresent_On401` (starting around line 145), including their doc comments — through the end of the second method.

These pin behaviour deliberately given up: `Asp.Versioning` reports versions only on responses that reach the endpoint. `AspVersioningIntegrationTests.Deprecation_HeadersAbsent_OnShortCircuitedResponse` (Task 3) pins the replacement behaviour.

- [ ] **Step 8: Run the whole suite**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx -v minimal`

Expected: `Failed: 0`. Total is roughly **310** — 308 baseline, minus 2 middleware/matcher test files (about 15 tests) and 2 short-circuit tests, plus 22 new ones. The exact number does not matter; **zero failures and zero build warnings do.**

If the build fails with `CS0246` on `ApiVersionDeprecationMiddleware` or `IApiPipelineVersionReader`, a reference was missed — search the solution for the name and remove it.

- [ ] **Step 9: Commit**

```bash
git add -A src tests
git commit -m "refactor!: delete the custom API version deprecation subsystem

BREAKING: removes WithVersionDeprecation(), UseApiVersionDeprecation(),
AddApiPipelineVersionDeprecation(), IApiPipelineVersionReader,
ApiPipelineConfigurationKeys.ApiVersionDeprecation, and
ApiPipelineServiceRegistrationOptions.AddApiPipelineVersionDeprecation.
ApiVersionDeprecationOptions moves to the ApiPipeline.NET.Versioning
namespace and loses PathPrefix.

Asp.Versioning 10 already emits Deprecation, Sunset and Link from its
own policy managers, and does it more completely — it also emits a
rel=deprecation link and api-deprecated-versions. Core now carries no
deprecation code at all.

Also removes the two tests pinning headers on short-circuited responses;
that behaviour is deliberately given up and its replacement is pinned in
AspVersioningIntegrationTests."
```

---

## Task 7: Update the sample application

**Files:**
- Modify: `samples/ApiPipeline.NET.Sample/Program.cs`
- Modify: `samples/ApiPipeline.NET.Sample/appsettings.json`
- Modify: `samples/ApiPipeline.NET.Sample/appsettings.Development.json`
- Modify: `samples/ApiPipeline.NET.Sample/appsettings.Production.json`
- Modify: `samples/ApiPipeline.NET.Sample/ConfigurationSnippets/minimal.json`

**Interfaces:**
- Consumes: the public surface after Task 6.
- Produces: a sample that compiles and demonstrates the new wiring.

- [ ] **Step 1: Fix the pipeline and versioning wiring**

In `samples/ApiPipeline.NET.Sample/Program.cs`:

Remove `.WithVersionDeprecation()` from the `app.UseApiPipeline(...)` call.

In the `AddApiVersioning` call, `ReportApiVersions = true` is already set — keep it, and add this comment above it so the requirement is visible:

```csharp
        // Required for deprecation headers: Asp.Versioning's reporter only writes Deprecation,
        // Sunset and Link when version reporting is on, either here or per version set.
        options.ReportApiVersions = true;
```

Replace the comment above `AddApiPipelineVersioning` with:

```csharp
// Binds ApiVersionDeprecationOptions and turns it into Asp.Versioning deprecation and sunset
// policies. Must come after AddApiVersioning: it decorates the reporter that call registers.
// The framework emits the headers; this package only supplies the dates from configuration.
builder.Services.AddApiPipelineVersioning(builder.Configuration);
```

- [ ] **Step 2: Remove `PathPrefix` from every config file**

Delete the `"PathPrefix": "/api",` line from the `ApiVersionDeprecationOptions` section in:

- `samples/ApiPipeline.NET.Sample/appsettings.json`
- `samples/ApiPipeline.NET.Sample/appsettings.Development.json`
- `samples/ApiPipeline.NET.Sample/appsettings.Production.json`
- `samples/ApiPipeline.NET.Sample/ConfigurationSnippets/minimal.json`

Not every file has the key; skip the ones that do not. Leave every other key untouched — the section shape is otherwise unchanged.

- [ ] **Step 3: Verify the sample builds and runs**

Run: `/usr/local/share/dotnet/dotnet build ApiPipeline.NET.slnx -v minimal`

Expected: build succeeds, zero warnings.

Then verify the headers actually appear end to end:

```bash
/usr/local/share/dotnet/dotnet run --project samples/ApiPipeline.NET.Sample --urls http://localhost:5199 &
sleep 8
curl -si http://localhost:5199/api/v1/orders | grep -iE "^(deprecation|sunset|link|api-deprecated-versions):"
kill %1
```

Expected: `Deprecation`, `Sunset`, `Link` and `api-deprecated-versions` headers are present. If they are absent, check that `ReportApiVersions` is true and that the controller's version is declared deprecated.

- [ ] **Step 4: Commit**

```bash
git add samples
git commit -m "docs(sample): wire deprecation through Asp.Versioning policies

Drops WithVersionDeprecation() and PathPrefix, and states why
ReportApiVersions must be on for the headers to appear."
```

---

## Task 8: Documentation and changelog

**Files:**
- Rewrite: `docs/features/version-deprecation.md`
- Modify: `docs/migration.md`
- Modify: `CHANGELOG.md`
- Modify: `README.md`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/INTERNALS.md`
- Modify: `docs/RUNBOOK.md`
- Modify: `docs/TROUBLESHOOTING.md`
- Modify: `samples/ApiPipeline.NET.Sample/README.md`

**Interfaces:**
- Consumes: the final public surface after Task 7.
- Produces: documentation matching the shipped code.

- [ ] **Step 1: Rewrite the feature doc**

Rewrite `docs/features/version-deprecation.md` to cover, in this order:

1. **What emits the headers** — `Asp.Versioning`'s `DefaultApiVersionReporter`, not this package. This package supplies policy values from configuration.
2. **Installation** — `dotnet add package ApiPipeline.NET.Versioning`; it is not a dependency of `ApiPipeline.NET` and must be installed explicitly. It brings `ApiPipeline.NET` and `Asp.Versioning.Http` transitively; applications using controllers add `Asp.Versioning.Mvc` themselves.
3. **Wiring** — `AddApiVersioning(...)` first, then `AddApiPipelineVersioning(configuration)`. State that the wrong order throws with a message naming the fix.
4. **Enabling reporting** — either `ReportApiVersions = true` globally or `.ReportApiVersions()` per version set. Note the startup warning when neither is obviously on.
5. **Configuration reference** — the section with `Enabled` and `DeprecatedVersions[]` (`Version`, `DeprecationDate`, `SunsetDate`, `SunsetLink`). State that `PathPrefix` was removed in 2.0.0 and why: policies resolve against the matched endpoint's declared version, so no path heuristic is needed.
6. **Emitted headers**, with the real measured values:

```http
Deprecation: @1751328000
Sunset: Thu, 01 Jan 2026 00:00:00 GMT
Link: <https://docs.example.com/v1-sunset>; rel="sunset"; title="sunset"; type="text/html"
api-deprecated-versions: 1.0
```

Note that `Deprecation` is RFC 9745's Structured Fields Date (`@` + Unix seconds), and that `rel="deprecation"` and `api-deprecated-versions` are new in 2.0.0.

7. **Behaviour change** — headers do **not** appear on short-circuited responses (401, 429, CORS preflight). In 1.x they did. Explain that `Asp.Versioning` reports only on responses that reach the endpoint, and that this was accepted rather than shimmed.
8. **Telemetry** — `apipipeline.deprecation.headers_added`, tagged `http.route`. Note that the `api_version` tag was removed in 2.0.0.
9. **Validation** — the four startup failures from Task 2, each with the message a reader would see.

- [ ] **Step 2: Add the migration section**

Add a section to `docs/migration.md` titled `API version deprecation (2.0.0)` covering:

| Before (1.x) | After (2.0.0) |
| --- | --- |
| `services.AddApiPipelineVersionDeprecation(config)` | `services.AddApiPipelineVersioning(config)` after `AddApiVersioning(...)` |
| `pipeline.WithVersionDeprecation()` | *(removed — delete the call)* |
| `app.UseApiVersionDeprecation()` | *(removed — delete the call)* |
| `IApiPipelineVersionReader` | *(removed — `Asp.Versioning` resolves versions)* |
| `ApiPipeline.NET.Options.ApiVersionDeprecationOptions` | `ApiPipeline.NET.Versioning.ApiVersionDeprecationOptions` |
| `ApiPipelineConfigurationKeys.ApiVersionDeprecation` | `VersioningConfigurationKeys.ApiVersionDeprecation` |
| `"PathPrefix": "/api"` | *(removed — delete the key)* |
| metric tag `api_version` | metric tag `http.route` |

Then three prose notes:

- **Version reporting must be on.** Set `ReportApiVersions = true` in `AddApiVersioning(...)`, or call `.ReportApiVersions()` on the version sets that need it. A warning is logged at startup if neither is obviously configured.
- **Headers no longer appear on short-circuited responses** (401, 429). See the feature doc.
- **Registration order matters.** `AddApiPipelineVersioning` throws if `AddApiVersioning` has not run.

- [ ] **Step 3: Add the changelog entries**

Under the existing `## [Unreleased] — targeting 2.0.0` heading in `CHANGELOG.md`, in the architecture-review follow-up group, add a `#### Removed` entry and extend `#### Changed`. Cover: the deleted public API list from Task 6's commit message, the `PathPrefix` removal, the metric tag change, the satellite's dependency change to `Asp.Versioning.Http`, the new `rel="deprecation"` and `api-deprecated-versions` headers, and the short-circuit behaviour change. Link to `docs/migration.md`.

- [ ] **Step 4: Update the remaining docs**

- `README.md` — the deprecation feature bullet now says the headers come from `Asp.Versioning` and that `ApiPipeline.NET.Versioning` is a separate opt-in package. Remove `WithVersionDeprecation()` from every pipeline example.
- `docs/ARCHITECTURE.md` — remove `ApiVersionDeprecationMiddleware` from the component inventory; add the four new Versioning components. Update the phase list.
- `docs/INTERNALS.md` — remove the phase from the ordering table and any `ApiVersionMatch` discussion.
- `docs/RUNBOOK.md` and `docs/TROUBLESHOOTING.md` — the "no deprecation headers" entry now checks, in order: is `ApiPipeline.NET.Versioning` installed; is `AddApiPipelineVersioning` called after `AddApiVersioning`; is `ReportApiVersions` on globally or per version set; is `ApiVersionDeprecationOptions:Enabled` true; is the endpoint's version actually declared deprecated. Add that short-circuited responses carry no headers by design.
- `samples/ApiPipeline.NET.Sample/README.md` — update the deprecation walkthrough to the new wiring.

- [ ] **Step 5: Verify no stale references remain**

```bash
grep -rn "WithVersionDeprecation\|UseApiVersionDeprecation\|AddApiPipelineVersionDeprecation\|IApiPipelineVersionReader\|ApiVersionMatch\|PathPrefix" \
  --include="*.cs" --include="*.json" --include="*.md" . \
  | grep -v "^./.worktrees" | grep -v "^./docs/superpowers" | grep -v "^./CHANGELOG.md" | grep -v "^./docs/migration.md"
```

Expected: no output. Hits in `CHANGELOG.md`, `docs/migration.md` and `docs/superpowers/` are correct — those describe the removal.

- [ ] **Step 6: Final full verification**

Run: `/usr/local/share/dotnet/dotnet test ApiPipeline.NET.slnx -c Release -v minimal`

Expected: `Failed: 0`, zero build warnings (`TreatWarningsAsErrors` makes any warning an error).

- [ ] **Step 7: Commit**

```bash
git add -A docs README.md CHANGELOG.md samples
git commit -m "docs: document delegation of deprecation headers to Asp.Versioning

Covers the new wiring and its ordering requirement, the ReportApiVersions
prerequisite, the removed PathPrefix key, the api_version to http.route
metric tag change, and the loss of headers on short-circuited responses."
```

---

## Self-Review

**Spec coverage.** §3.1 ownership → Task 6. §3.2 components → Tasks 2, 3, 4, 5. §3.3 data flow → Task 3. §3.4 packaging and the `Asp.Versioning.Http` reduction → Task 1. §3.5 registration order → Task 5 Steps 5 and 7. §4 configuration contract and §4.1 validation → Task 2. §4.2 startup warning → Task 4. §5 telemetry → Task 5. §6 RFC 9745 → Task 3 Step 1 (`Deprecation_HeaderValue_IsRfc9745StructuredFieldDate`). §6a short-circuit regression → Task 3 Step 1 and Task 6 Step 7. §7 removals → Task 6. §8 testing → every task. §9 documentation → Task 8. §10 project files → Task 1 and Task 7. §11 risks → all mitigations have a task. No gaps.

**Placeholders.** None. Every code step carries the literal code; every doc step in Task 8 enumerates the required content rather than saying "update the docs".

**Type consistency.** `ApiVersionDeprecationOptions` / `DeprecatedVersion` defined in Task 2 and consumed with identical property names in Tasks 3, 4, 5. `VersioningConfigurationKeys.ApiVersionDeprecation` defined in Task 2, used in Task 3. `AddApiPipelineVersioning(IServiceCollection, IConfiguration)` defined in Task 3 and extended in Tasks 4 and 5 — never renamed. `RecordDeprecationHeadersAdded(string? route)` changed in Task 5 Step 3 and called with that signature in Task 5 Step 4. `CapturingLoggerProvider.Messages` is the existing test helper, matching its real shape. `IReportApiVersions` members `Report(HttpResponse, ApiVersionModel)` and `Mapping` match the assembly, verified against `Asp.Versioning.Abstractions` 10.0.0.

**One ordering note for the executor:** Task 2 leaves two `ApiVersionDeprecationOptions` types in the solution, in different namespaces. This is intentional and resolves in Task 6. Do not "fix" it early — deleting the core type before Task 6 breaks the still-present middleware.
