using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>PreflightMaxAge</c>, which lets a browser cache a preflight instead of sending one before every
/// call, and the response headers a browser client needs to back off from a 429.
/// </summary>
public sealed class CorsPreflightAndExposureTests
{
    private const string Origin = "https://app.example.com";

    private static Dictionary<string, string?> CorsConfig(Action<Dictionary<string, string?>>? configure = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = Origin;
            configure?.Invoke(c);
        });

    private static async Task<HttpClient> StartAsync(WebApplication app)
    {
        app.UseApiPipeline(pipeline => pipeline.WithCors());
        app.MapPost("/test", () => Results.Ok("ok"));
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app.GetTestClient();
    }

    private static Task<HttpResponseMessage> PreflightAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/test");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A configured max age is sent on the preflight, in whole seconds.</summary>
    [Fact]
    public async Task PreflightMaxAge_IsSentOnThePreflight()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            CorsConfig(c => c["ApiPipeline:Cors:PreflightMaxAge"] = "00:10:00"), Environments.Production);
        var client = await StartAsync(app);

        var response = await PreflightAsync(client);

        response.Headers.GetValues("Access-Control-Max-Age").Should().Equal("600");
    }

    /// <summary>Unset, no header is sent and the browser's own default (5 seconds in Chromium) applies.</summary>
    [Fact]
    public async Task PreflightMaxAge_Unset_SendsNoHeader()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(CorsConfig(), Environments.Production);
        var client = await StartAsync(app);

        var response = await PreflightAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Contains("Access-Control-Max-Age").Should().BeFalse();
    }

    /// <summary>
    /// The header counts whole seconds, so a fraction would be sent as a decimal a browser ignores;
    /// browsers cap it at 2 hours (Chromium) or 24 hours (Firefox), so more than a day is a mistake.
    /// </summary>
    [Theory]
    [InlineData("-00:00:01")]
    [InlineData("00:00:01.500")]
    [InlineData("1.00:00:01")]
    public async Task PreflightMaxAge_OutOfRange_FailsStartup(string value)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            CorsConfig(c => c["ApiPipeline:Cors:PreflightMaxAge"] = value), Environments.Production);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*PreflightMaxAge*");
    }

    /// <summary>
    /// <c>Retry-After</c> is not a CORS-safelisted response header, nor are the IETF rate-limit
    /// fields, so a browser client cannot read when to retry a 429 unless they are exposed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitHeaders_AreExposed(bool allowAllInDevelopment)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(
            CorsConfig(c => c["ApiPipeline:Cors:AllowAllInDevelopment"] = allowAllInDevelopment ? "true" : "false"),
            Environments.Development);
        var client = await StartAsync(app);
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", Origin);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"))
            .Split(',', StringSplitOptions.TrimEntries);
        exposed.Should().Contain(["Retry-After", "RateLimit", "RateLimit-Policy"]);
    }
}
