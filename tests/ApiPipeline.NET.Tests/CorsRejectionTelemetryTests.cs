using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>apipipeline.cors.rejected</c> counts origins missing from the allow-list, and counting does
/// not change which origins match.
/// </summary>
public sealed class CorsRejectionTelemetryTests
{
    private const string CounterName = "apipipeline.cors.rejected";

    private static Dictionary<string, string?> Config(params string[] origins) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowAllInDevelopment"] = "false";
            for (var i = 0; i < origins.Length; i++)
            {
                c[$"ApiPipeline:Cors:AllowedOrigins:{i}"] = origins[i];
            }
        });

    /// <summary>Sends one request from <paramref name="origin"/>; returns the response and the rejections this host counted.</summary>
    private static async Task<(HttpResponseMessage Response, long Rejections)> SendWithOriginAsync(
        Dictionary<string, string?> config,
        string origin)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        using var rejections = new MetricRecorder<long>(app.Services, CounterName);
        app.UseRouting();
        app.UseApiPipeline(pipeline => pipeline.WithCors());
        app.MapGet("/resource", () => Results.Ok("ok"));
        await app.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/resource");
        request.Headers.Add("Origin", origin);
        var response = await app.GetTestClient().SendAsync(request);
        return (response, rejections.Measurements.Sum(m => m.Value));
    }

    /// <summary>An origin missing from a populated allow-list is counted.</summary>
    [Fact]
    public async Task DisallowedOrigin_AgainstConfiguredList_IncrementsTheCounter()
    {
        var (response, rejections) = await SendWithOriginAsync(
            Config("https://portal.example.com"), "https://evil.example.com");

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        rejections.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// An allowed origin must still be allowed — the counting wrapper must not change which origins
    /// match — and must not be counted as a rejection.
    /// </summary>
    [Fact]
    public async Task AllowedOrigin_IsStillAllowed_AndNotCounted()
    {
        var (response, rejections) = await SendWithOriginAsync(
            Config("https://portal.example.com"), "https://portal.example.com");

        response.Headers.GetValues("Access-Control-Allow-Origin")
            .Should().ContainSingle().Which.Should().Be("https://portal.example.com");
        rejections.Should().Be(0);
    }

    /// <summary>
    /// <c>WithOrigins</c> normalizes the origins it stores (scheme and host are lower-cased), and
    /// the default matcher compares against the normalized form. The wrapper delegates to that
    /// matcher rather than re-comparing raw configuration strings, so a mixed-case entry in
    /// configuration still matches the origin a browser sends.
    /// </summary>
    [Fact]
    public async Task MixedCaseConfiguredOrigin_StillMatches_TheNormalizedOrigin()
    {
        var (response, rejections) = await SendWithOriginAsync(
            Config("https://Portal.Example.com"), "https://portal.example.com");

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        rejections.Should().Be(0);
    }

    /// <summary>
    /// A wildcard allow-list is resolved by <c>CorsPolicy.AllowAnyOrigin</c> before the matcher is
    /// consulted, so it must keep allowing every origin and must count nothing.
    /// </summary>
    [Fact]
    public async Task WildcardOrigin_AllowsAnyOrigin_AndCountsNothing()
    {
        var (response, rejections) = await SendWithOriginAsync(Config("*"), "https://anything.example.com");

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        rejections.Should().Be(0);
    }

    /// <summary>
    /// The pre-existing behaviour for an enabled policy with no origins at all — reject and count —
    /// is unchanged.
    /// </summary>
    [Fact]
    public async Task NoConfiguredOrigins_RejectsAndCounts()
    {
        var (response, rejections) = await SendWithOriginAsync(Config(), "https://portal.example.com");

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        rejections.Should().BeGreaterThan(0);
    }
}
