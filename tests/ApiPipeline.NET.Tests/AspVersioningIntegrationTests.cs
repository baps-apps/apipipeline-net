using System.Globalization;
using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// End-to-end tests of the deprecation headers on the wire. <c>Asp.Versioning</c> writes them, so
/// these fail if the framework changes what it emits.
/// </summary>
public sealed class AspVersioningIntegrationTests
{
    private static readonly DateTimeOffset DeprecatedOn =
        new(2025, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SunsetOn =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, string?> DeprecationConfig(bool enabled = true) => new()
    {
        ["ApiPipeline:VersionDeprecation:Enabled"] = enabled ? "true" : "false",
        ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:Version"] = "1.0",
        ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:DeprecationDate"] = "2025-07-01T00:00:00Z",
        ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:SunsetDate"] = "2026-01-01T00:00:00Z",
        ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:DeprecationLink"] = "https://docs.example.com/v1-deprecation",
        ["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:SunsetLink"] = "https://docs.example.com/v1-sunset",
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
        builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
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
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        var expected = "@" + DeprecatedOn.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        response.Headers.GetValues("Deprecation").Single().Should().Be(expected);
    }

    /// <summary>All three headers reach a caller of a deprecated version.</summary>
    [Fact]
    public async Task DeprecatedVersion_Request_EmitsDeprecationSunsetAndLink()
    {
        await using var app = CreateApp(DeprecationConfig());
        MapVersionedEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        response.Headers.Contains("Deprecation").Should().BeTrue();
        response.Headers.GetValues("Sunset").Single()
            .Should().Be(SunsetOn.ToString("R", CultureInfo.InvariantCulture));
        var links = response.Headers.GetValues("Link").SelectMany(v => v.Split(',')).Select(l => l.Trim()).ToList();
        links.Should().ContainSingle(l => l.Contains("rel=\"deprecation\"", StringComparison.Ordinal))
            .Which.Should().Contain("https://docs.example.com/v1-deprecation", "each link carries its own relation");
        links.Should().ContainSingle(l => l.Contains("rel=\"sunset\"", StringComparison.Ordinal))
            .Which.Should().Contain("https://docs.example.com/v1-sunset");
    }

    /// <summary>A caller on a supported version must not be warned.</summary>
    [Fact]
    public async Task NonDeprecatedVersion_Request_EmitsNoDeprecationHeaders()
    {
        await using var app = CreateApp(DeprecationConfig());
        MapVersionedEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/api/v2/orders-v2", TestContext.Current.CancellationToken);

        response.Headers.Contains("Deprecation").Should().BeFalse();
        response.Headers.Contains("Sunset").Should().BeFalse();
    }

    /// <summary>The off switch is honoured: no policies are registered, so nothing is emitted.</summary>
    [Fact]
    public async Task Disabled_EmitsNothing()
    {
        await using var app = CreateApp(DeprecationConfig(enabled: false));
        MapVersionedEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        response.Headers.Contains("Deprecation").Should().BeFalse();
        response.Headers.Contains("Sunset").Should().BeFalse();
    }

    /// <summary>Every spelling routing accepts for a version resolves to the same configured policy.</summary>
    [Theory]
    [InlineData("/api/v1/orders")]
    [InlineData("/api/v1.0/orders")]
    public async Task VersionSpellingVariants_AllMatch(string path)
    {
        await using var app = CreateApp(DeprecationConfig());
        MapVersionedEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken);

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
        config["ApiPipeline:VersionDeprecation:DeprecatedVersions:0:Version"] = "not-a-version";

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

        await app.StartAsync(TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders");
        request.Headers.Add("X-Short-Circuit", "1");
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.Contains("Deprecation").Should()
            .BeFalse("Asp.Versioning reports versions only on responses that reach the endpoint");
    }
}
