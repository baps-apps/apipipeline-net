using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// RFC 6797 §7.2: an HSTS host must not send <c>Strict-Transport-Security</c> over a non-secure
/// transport. Behind a TLS-terminating ingress this matters in practice — without forwarded headers
/// applied, every request arrives as plain HTTP.
/// </summary>
public sealed class SecurityHeadersTransportTests
{
    private static Dictionary<string, string?> Config() =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
        });

    /// <summary>
    /// HSTS must be omitted on a plaintext HTTP request.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_HSTS_IsNotSent_OverPlainHttp()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/ok", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/ok", TestContext.Current.CancellationToken);

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    /// <summary>
    /// HSTS must still be sent over HTTPS — including when the scheme was established by forwarded
    /// headers from a trusted proxy, which is the normal Kubernetes case.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_HSTS_IsSent_OverHttps()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/ok", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/ok", TestContext.Current.CancellationToken);

        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue();
    }

    /// <summary>
    /// Transport-independent headers must still be applied over plain HTTP.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_NonTransportHeaders_AreSent_OverPlainHttp()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(Config(), Environments.Production);
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/ok", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetApiTestClient().GetAsync("/ok", TestContext.Current.CancellationToken);

        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
        response.Headers.Contains("X-Frame-Options").Should().BeTrue();
        response.Headers.Contains("Referrer-Policy").Should().BeTrue();
    }
}
