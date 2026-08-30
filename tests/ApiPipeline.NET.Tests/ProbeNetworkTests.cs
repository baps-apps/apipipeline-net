using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The probe exemption also needs the caller in <c>ProbeNetworks</c>: behind a layer-4 load balancer
/// an internet caller has no proxy header either.
/// </summary>
public sealed class ProbeNetworkTests
{
    public static TheoryData<string> PublicAddresses => ["198.51.100.7", "2001:db8::7", "::ffff:198.51.100.7"];

    public static TheoryData<string> PrivateAddresses =>
    [
        "127.0.0.1", "10.1.2.3", "172.20.0.4", "192.168.1.10", "100.64.0.9", "::1", "fd00::5", "::ffff:10.1.2.3",
    ];

    [Theory]
    [MemberData(nameof(PublicAddresses))]
    public async Task RateLimiting_DirectProbeFromAPublicAddress_IsLimited(string address)
    {
        await using var app = await MainLimiterAppAsync(address);
        var client = app.GetTestClient();

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(
            (HttpStatusCode)429, "a caller outside ProbeNetworks is not a kubelet, header or no header");
    }

    [Theory]
    [MemberData(nameof(PrivateAddresses))]
    public async Task RateLimiting_DirectProbeFromAPrivateAddress_IsNotLimited(string address)
    {
        await using var app = await MainLimiterAppAsync(address);
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task EdgeRateLimiting_DirectProbeFromAPublicAddress_IsLimited()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:EdgeRateLimiting:Enabled"] = "true";
            c["ApiPipeline:EdgeRateLimiting:PermitLimit"] = "1";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:01:00";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
            return next(context);
        });
        app.UseApiPipeline(pipeline => pipeline.WithEdgeRateLimiting());
        app.MapGet("/health", () => Results.Ok("healthy"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429);
    }

    /// <summary>A configured list replaces the defaults, like every list the package reads.</summary>
    [Fact]
    public async Task RateLimiting_ConfiguredProbeNetworks_ReplaceTheDefaults()
    {
        var config = TestAppBuilder.WithRateLimiting(permitLimit: 1);
        config["ApiPipeline:RateLimiting:ProbeNetworks:0"] = "203.0.113.0/24";

        await using (var app = await MainLimiterAppAsync("203.0.113.5", config))
        {
            var client = app.GetTestClient();
            for (var i = 0; i < 3; i++)
            {
                (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
            }
        }

        await using (var app = await MainLimiterAppAsync("10.1.2.3", config))
        {
            var client = app.GetTestClient();
            (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(
                (HttpStatusCode)429, "the configured list replaced the private-range default");
        }
    }

    /// <summary>
    /// No peer address (a Unix socket or named pipe listener) cannot come from the network directly,
    /// so it keeps the exemption.
    /// </summary>
    [Fact]
    public async Task RateLimiting_DirectProbeWithNoPeerAddress_IsNotLimited()
    {
        await using var app = await MainLimiterAppAsync(address: null);
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Out-of-range prefixes fail, and so does shorthand or octal IPv4, which the framework would
    /// read as another network (10/8 is 0.0.0.0/8, 010.0.0.0/8 is 8.0.0.0/8).
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("10/8")]
    [InlineData("010.0.0.0/8")]
    public async Task ProbeNetworks_InvalidEntry_FailsValidation(string entry)
    {
        var config = TestAppBuilder.WithRateLimiting();
        config["ApiPipeline:RateLimiting:ProbeNetworks:0"] = entry;
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        var act = () => app.Services.GetRequiredService<IOptions<ApiPipelineRateLimitingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*ProbeNetworks*'{entry}'*");
    }

    [Fact]
    public void ProbeNetworks_DefaultsToNull_WithPrivateRangeDefaults()
    {
        new ApiPipelineRateLimitingOptions().ProbeNetworks.Should().BeNull();
        ApiPipelineRateLimitingOptions.DefaultProbeNetworks.Should().Equal(
            "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10",
            "::1/128", "fc00::/7", "fe80::/10");
    }

    private static async Task<WebApplication> MainLimiterAppAsync(string? address, Dictionary<string, string?>? config = null)
    {
        var app = await TestAppBuilder.CreateAppAsync(config ?? TestAppBuilder.WithRateLimiting(permitLimit: 1));
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
            return next(context);
        });
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/health", () => Results.Ok("healthy"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
