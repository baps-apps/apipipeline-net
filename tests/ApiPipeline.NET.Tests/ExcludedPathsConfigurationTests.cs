using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The three <c>ExcludedPaths</c> lists (rate limiting, edge rate limiting, compression) follow one
/// set of rules: each entry must be a path, surrounding spaces are ignored, and a configured list,
/// even an empty one, replaces the default. The same goes for the CORS lists whose empty form is
/// rejected.
/// </summary>
public sealed class ExcludedPathsConfigurationTests
{
    // A node address: the probe exemption also needs the peer in ProbeNetworks (private ranges by default).
    private const string Peer = "10.1.2.3";

    private static Dictionary<string, string?> EdgeConfig(Action<Dictionary<string, string?>>? configure = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:10:00";
            configure?.Invoke(c);
        });

    private static Dictionary<string, string?> CompressionConfig(Action<Dictionary<string, string?>>? configure = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCompression:Enabled"] = "true";
            c["ApiPipeline:ResponseCompression:EnableForHttps"] = "true";
            configure?.Invoke(c);
        });

    private static async Task<HttpStatusCode> GetFromPeerAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestPeerAddress.HeaderName, Peer);
        return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    private static async Task<HttpClient> StartAsync(WebApplication app, Action<ApiPipelineBuilder> pipeline, params string[] paths)
    {
        app.UseSimulatedPeerAddress();
        app.UseApiPipeline(pipeline);
        foreach (var path in paths)
        {
            app.MapGet(path, () => Results.Ok(new string('x', 2000)));
        }

        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
        return client;
    }

    /// <summary>An entry that is not a path fails validation, for every limiter and compression.</summary>
    [Theory]
    [InlineData("ApiPipeline:EdgeRateLimiting:ExcludedPaths:0")]
    [InlineData("ApiPipeline:ResponseCompression:ExcludedPaths:0")]
    public async Task ExcludedPath_ThatIsNotAPath_FailsStartup(string key)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            EdgeConfig(c => c[key] = "internal/health").Also(c => c["ApiPipeline:ResponseCompression:Enabled"] = "true"));
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .WithMessage("*ExcludedPaths entry 'internal/health'*");
    }

    /// <summary>Surrounding spaces, easy to leave in a config file, are ignored as in the main limiter's list.</summary>
    [Fact]
    public async Task EdgeExcludedPath_WithSurroundingSpaces_IsExcluded()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(EdgeConfig(c => c["ApiPipeline:EdgeRateLimiting:ExcludedPaths:0"] = " /status "));
        var client = await StartAsync(app, p => p.WithEdgeRateLimiting(), "/status", "/test");

        for (var i = 0; i < 5; i++)
        {
            (await GetFromPeerAsync(client, "/status")).Should().Be(HttpStatusCode.OK);
        }

        (await GetFromPeerAsync(client, "/test")).Should().Be(HttpStatusCode.OK);
        ((int)await GetFromPeerAsync(client, "/test")).Should().Be(429, "the control path is limited");
    }

    /// <summary>The same for compression: an untrimmed entry is trimmed.</summary>
    [Fact]
    public async Task CompressionExcludedPath_WithSurroundingSpaces_IsExcluded()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(CompressionConfig(c => c["ApiPipeline:ResponseCompression:ExcludedPaths:0"] = " /data "));
        var client = await StartAsync(app, p => p.WithResponseCompression(), "/data", "/test");

        (await client.GetAsync("/data", TestContext.Current.CancellationToken)).Content.Headers.ContentEncoding.Should().BeEmpty();
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).Content.Headers.ContentEncoding.Should().Contain("gzip");
    }

    /// <summary>
    /// <c>"ExcludedPaths": []</c> replaces the default, as documented: <c>/health</c> is then limited.
    /// </summary>
    [Fact]
    public async Task MainLimiter_EmptyJsonList_ReplacesTheDefault()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithRateLimiting(permitLimit: 1),
            json: """{ "ApiPipeline": { "RateLimiting": { "ExcludedPaths": [] } } }""");
        var client = await StartAsync(app, p => p.WithRateLimiting(), "/health");

        (await GetFromPeerAsync(client, "/health")).Should().Be(HttpStatusCode.OK);
        ((int)await GetFromPeerAsync(client, "/health")).Should().Be(429);
    }

    /// <summary>The edge limiter's list, the same way.</summary>
    [Fact]
    public async Task EdgeLimiter_EmptyJsonList_ReplacesTheDefault()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            EdgeConfig(),
            json: """{ "ApiPipeline": { "EdgeRateLimiting": { "ExcludedPaths": [] } } }""");
        var client = await StartAsync(app, p => p.WithEdgeRateLimiting(), "/health");

        (await GetFromPeerAsync(client, "/health")).Should().Be(HttpStatusCode.OK);
        ((int)await GetFromPeerAsync(client, "/health")).Should().Be(429);
    }

    /// <summary>And compression's: <c>/health</c> is compressed once the list is emptied.</summary>
    [Fact]
    public async Task Compression_EmptyJsonList_ReplacesTheDefault()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            CompressionConfig(),
            json: """{ "ApiPipeline": { "ResponseCompression": { "ExcludedPaths": [] } } }""");
        var client = await StartAsync(app, p => p.WithResponseCompression(), "/health");

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).Content.Headers.ContentEncoding.Should().Contain("gzip");
    }

    /// <summary>
    /// An environment variable cannot hold a list, but it can hold an empty value
    /// (<c>ApiPipeline__RateLimiting__ExcludedPaths=</c>), which reaches the binder the same way.
    /// </summary>
    [Fact]
    public async Task MainLimiter_EmptyValue_ReplacesTheDefault()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.WithRateLimiting(permitLimit: 1).Also(c => c["ApiPipeline:RateLimiting:ExcludedPaths"] = ""));
        var client = await StartAsync(app, p => p.WithRateLimiting(), "/health");

        (await GetFromPeerAsync(client, "/health")).Should().Be(HttpStatusCode.OK);
        ((int)await GetFromPeerAsync(client, "/health")).Should().Be(429);
    }

    /// <summary>
    /// An empty CORS method or header list in JSON fails startup instead of reading as "use the defaults".
    /// </summary>
    [Theory]
    [InlineData("AllowedMethods")]
    [InlineData("AllowedHeaders")]
    public async Task Cors_EmptyJsonList_FailsStartup(string key)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            TestAppBuilder.MinimalConfig(c =>
            {
                c["ApiPipeline:Cors:Enabled"] = "true";
                c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com";
            }),
            Environments.Production,
            json: $$"""{ "ApiPipeline": { "Cors": { "{{key}}": [] } } }""");
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage($"*{key}*");
    }
}
