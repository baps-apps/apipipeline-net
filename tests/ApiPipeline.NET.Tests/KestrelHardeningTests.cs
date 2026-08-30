using FluentAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the Kestrel connection and timeout limits that defend against slow-request
/// (slowloris) and connection-exhaustion attacks.
/// TestServer does not run Kestrel, so these check the values written to
/// <see cref="KestrelServerOptions"/>.
/// </summary>
public sealed class KestrelHardeningTests
{
    private static async Task<KestrelServerOptions> ResolveKestrelOptionsAsync(
        Dictionary<string, string?> config)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");
        await app.StartAsync();

        var kestrelOptions = new KestrelServerOptions();
        foreach (var configurator in app.Services.GetServices<IConfigureOptions<KestrelServerOptions>>())
        {
            configurator.Configure(kestrelOptions);
        }

        return kestrelOptions;
    }

    /// <summary>
    /// A client that opens a connection and dribbles request headers forever holds a Kestrel
    /// connection until <c>RequestHeadersTimeout</c> expires. The framework default is 30s;
    /// operators must be able to lower it from configuration.
    /// </summary>
    [Fact]
    public async Task RequestHeadersTimeout_IsAppliedFromConfiguration()
    {
        var options = await ResolveKestrelOptionsAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:RequestHeadersTimeout"] = "00:00:05";
        }));

        options.Limits.RequestHeadersTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Idle keep-alive connections are a cheap way to occupy a pod's connection budget.
    /// </summary>
    [Fact]
    public async Task KeepAliveTimeout_IsAppliedFromConfiguration()
    {
        var options = await ResolveKestrelOptionsAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:KeepAliveTimeout"] = "00:00:30";
        }));

        options.Limits.KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Kestrel's default is unlimited concurrent connections, so one client can exhaust a pod's
    /// sockets and file handles before any middleware in this package runs.
    /// </summary>
    [Fact]
    public async Task MaxConcurrentConnections_IsAppliedFromConfiguration()
    {
        var options = await ResolveKestrelOptionsAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxConcurrentConnections"] = "2000";
        }));

        options.Limits.MaxConcurrentConnections.Should().Be(2000);
    }

    /// <summary>
    /// The classic slowloris body attack: send a large Content-Length one byte at a time.
    /// <c>MinRequestBodyDataRate</c> is the framework's defence and must be configurable together
    /// with its grace period.
    /// </summary>
    [Fact]
    public async Task MinRequestBodyDataRate_IsAppliedFromConfiguration()
    {
        var options = await ResolveKestrelOptionsAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MinRequestBodyDataRateBytesPerSecond"] = "480";
            c["ApiPipeline:RequestLimits:MinRequestBodyDataRateGracePeriod"] = "00:00:10";
        }));

        options.Limits.MinRequestBodyDataRate.Should().NotBeNull();
        options.Limits.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(480);
        options.Limits.MinRequestBodyDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Setting the rate to 0 is the documented way to disable Kestrel's minimum-rate enforcement
    /// (needed for legitimately slow uploads over poor links). It must produce a null rate rather
    /// than a rate of zero, which Kestrel rejects.
    /// </summary>
    [Fact]
    public async Task MinRequestBodyDataRate_Zero_DisablesTheLimit()
    {
        var options = await ResolveKestrelOptionsAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MinRequestBodyDataRateBytesPerSecond"] = "0";
        }));

        options.Limits.MinRequestBodyDataRate.Should().BeNull();
    }

    /// <summary>
    /// Every limit here is gated on <c>Enabled</c>, like the existing size limits: an operator who
    /// turns the section off must get the framework defaults back, not a half-applied mixture.
    /// </summary>
    [Fact]
    public async Task TimeoutLimits_AreNotApplied_WhenRequestLimitsDisabled()
    {
        var frameworkDefaults = new KestrelServerOptions();
        var options = await ResolveKestrelOptionsAsync(TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "false";
            c["ApiPipeline:RequestLimits:RequestHeadersTimeout"] = "00:00:05";
            c["ApiPipeline:RequestLimits:KeepAliveTimeout"] = "00:00:30";
            c["ApiPipeline:RequestLimits:MaxConcurrentConnections"] = "2000";
        }));

        options.Limits.RequestHeadersTimeout.Should().Be(frameworkDefaults.Limits.RequestHeadersTimeout);
        options.Limits.KeepAliveTimeout.Should().Be(frameworkDefaults.Limits.KeepAliveTimeout);
        options.Limits.MaxConcurrentConnections.Should().Be(frameworkDefaults.Limits.MaxConcurrentConnections);
    }

    /// <summary>
    /// A grace period with no rate is a configuration mistake that silently does nothing —
    /// Kestrel's <c>MinDataRate</c> needs both halves. Fail at startup instead.
    /// </summary>
    [Fact]
    public async Task MinRequestBodyDataRateGracePeriod_WithoutRate_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MinRequestBodyDataRateGracePeriod"] = "00:00:10";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*MinRequestBodyDataRateBytesPerSecond*");
    }
}
