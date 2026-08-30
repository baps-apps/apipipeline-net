using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Configurations that are valid, start cleanly, and then do nothing. Each one has cost an
/// operator an afternoon somewhere, so each gets a startup warning that names the cause.
/// </summary>
public sealed class StartupWarningTests
{
    private static async Task<List<string>> StartAndCaptureAsync(
        Dictionary<string, string?> config,
        Action<WebApplication> configurePipeline,
        string environment = "Production")
    {
        var capture = new CapturingLoggerProvider();
        await using var app = await TestAppBuilder.CreateAppAsync(
            config,
            environment,
            configureServices: services => services.AddSingleton<ILoggerProvider>(capture));
        configurePipeline(app);
        app.MapGet("/test", () => "ok");
        await app.StartAsync();
        return capture.Messages;
    }

    /// <summary>
    /// The trap this package walks its own consumers into: <c>EnableForHttps</c> defaults to
    /// <c>false</c> for BREACH reasons, and the deployment this package targets — TLS terminated at
    /// an ingress with <c>X-Forwarded-Proto</c> applied — makes every request HTTPS as far as the
    /// server is concerned. Compression is then enabled, valid, and completely inert.
    /// </summary>
    [Fact]
    public async Task ResponseCompression_EnabledButNotForHttps_WarnsThatNothingWillBeCompressed()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.MinimalConfig(c =>
            {
                c["ApiPipeline:ResponseCompression:Enabled"] = "true";
                c["ApiPipeline:ResponseCompression:EnableForHttps"] = "false";
            }),
            app => app.UseApiPipeline(pipeline => pipeline.WithResponseCompression()));

        messages.Should().Contain(m =>
            m.Contains("EnableForHttps", StringComparison.Ordinal)
            && m.Contains("no response will be compressed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// With compression actually usable there is nothing to warn about — only the existing BREACH
    /// advisory, which is a different message. A warning that fires in the correct configuration is
    /// noise, and noise is why operators stop reading logs.
    /// </summary>
    [Fact]
    public async Task ResponseCompression_EnabledForHttps_DoesNotWarnAboutInertCompression()
    {
        var messages = await StartAndCaptureAsync(
            TestAppBuilder.MinimalConfig(c =>
            {
                c["ApiPipeline:ResponseCompression:Enabled"] = "true";
                c["ApiPipeline:ResponseCompression:EnableForHttps"] = "true";
            }),
            app => app.UseApiPipeline(pipeline => pipeline.WithResponseCompression()));

        messages.Should().NotContain(m =>
            m.Contains("no response will be compressed", StringComparison.OrdinalIgnoreCase));
    }
}
