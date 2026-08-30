using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Request limits under IIS (in-process) and HTTP.sys, which do not read Kestrel's options: the
/// limits each server has an option for are applied to it, and the ones it has none for are named
/// in a startup warning instead of being silently ignored.
/// </summary>
public sealed class RequestLimitsServerTests
{
    private static Dictionary<string, string?> LimitsConfig(bool enabled = true) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = enabled ? "true" : "false";
            c["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "1048576";
            c["ApiPipeline:RequestLimits:MaxConcurrentConnections"] = "500";
            c["ApiPipeline:RequestLimits:RequestHeadersTimeout"] = "00:00:15";
            c["ApiPipeline:RequestLimits:KeepAliveTimeout"] = "00:01:00";
        });

    /// <summary>IIS in-process reads the body limit from its own options.</summary>
    [Fact]
    public async Task Iis_GetsTheBodyLimit()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(LimitsConfig());

        app.Services.GetRequiredService<IOptions<IISServerOptions>>().Value.MaxRequestBodySize.Should().Be(1_048_576);
    }

    /// <summary>HTTP.sys has options for the body limit, connections and both timeouts.</summary>
    [Fact]
    public async Task HttpSys_GetsTheLimitsItHasOptionsFor()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(LimitsConfig());

        var options = app.Services.GetRequiredService<IOptions<HttpSysOptions>>().Value;

        options.MaxRequestBodySize.Should().Be(1_048_576);
        options.MaxConnections.Should().Be(500);
        options.Timeouts.HeaderWait.Should().Be(TimeSpan.FromSeconds(15));
        options.Timeouts.IdleConnection.Should().Be(TimeSpan.FromMinutes(1));
    }

    /// <summary>Disabled, the servers' own defaults are left alone, as Kestrel's are.</summary>
    [Fact]
    public async Task Disabled_LeavesTheServersDefaults()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(LimitsConfig(enabled: false));

        app.Services.GetRequiredService<IOptions<IISServerOptions>>().Value.MaxRequestBodySize
            .Should().Be(new IISServerOptions().MaxRequestBodySize);
        app.Services.GetRequiredService<IOptions<HttpSysOptions>>().Value.MaxRequestBodySize
            .Should().Be(new HttpSysOptions().MaxRequestBodySize);
    }

    /// <summary>
    /// What each server cannot apply from code: IIS takes header limits and timeouts from its own
    /// configuration (web.config, applicationHost.config), HTTP.sys takes header limits from the
    /// registry, and neither has a minimum body data rate.
    /// </summary>
    [Fact]
    public void Unenforced_NamesWhatEachServerCannotApply()
    {
        var options = new ApiPipelineRequestLimitsOptions
        {
            Enabled = true,
            MaxRequestBodySize = 1,
            MaxRequestHeadersTotalSize = 1,
            MaxRequestHeaderCount = 1,
            RequestHeadersTimeout = TimeSpan.FromSeconds(5),
            KeepAliveTimeout = TimeSpan.FromSeconds(5),
            MaxConcurrentConnections = 1,
            MinRequestBodyDataRateBytesPerSecond = 100,
        };

        RequestLimitsServerSupport.Unenforced(RequestLimitsServerSupport.Server.Iis, options).Should().BeEquivalentTo(
            "MaxRequestHeadersTotalSize", "MaxRequestHeaderCount", "RequestHeadersTimeout", "KeepAliveTimeout",
            "MaxConcurrentConnections", "MinRequestBodyDataRateBytesPerSecond");
        RequestLimitsServerSupport.Unenforced(RequestLimitsServerSupport.Server.HttpSys, options).Should().BeEquivalentTo(
            "MaxRequestHeadersTotalSize", "MaxRequestHeaderCount", "MinRequestBodyDataRateBytesPerSecond");
        RequestLimitsServerSupport.Unenforced(RequestLimitsServerSupport.Server.Kestrel, options).Should().BeEmpty();
    }

    /// <summary>Only settings that are set are reported: an unset limit is not being ignored.</summary>
    [Fact]
    public void Unenforced_IgnoresUnsetSettings() =>
        RequestLimitsServerSupport.Unenforced(
                RequestLimitsServerSupport.Server.Iis,
                new ApiPipelineRequestLimitsOptions { Enabled = true, MaxRequestBodySize = 1 })
            .Should().BeEmpty();

    /// <summary>
    /// The availability checks name the real types: a typo would silently drop IIS and HTTP.sys
    /// support on every runtime, not only source-built ones.
    /// </summary>
    [Fact]
    public void ServerOptionTypes_AreFoundOnMicrosoftBuiltRuntimes()
    {
        RequestLimitsServerSupport.IisOptionsAvailable.Should().BeTrue();
        RequestLimitsServerSupport.HttpSysOptionsAvailable.Should().BeTrue();
    }

    /// <summary>
    /// A source-built runtime ships the IIS and HTTP.sys assemblies as empty stubs: their
    /// configurators are skipped instead of throwing TypeLoadException from AddApiPipeline.
    /// </summary>
    [Fact]
    public void ServerOptionTypesMissing_SkipsTheirDefaults()
    {
        var services = new ServiceCollection();

        services.AddServerSpecificDefaults(iis: false, httpSys: false);

        services.Should().NotContain(d => d.ServiceType == typeof(IConfigureOptions<IISServerOptions>));
        services.Should().NotContain(d => d.ServiceType == typeof(IConfigureOptions<HttpSysOptions>));
    }

    /// <summary>The server is recognised by the assembly its <c>IServer</c> comes from.</summary>
    [Theory]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel.Core", "Kestrel")]
    [InlineData("Microsoft.AspNetCore.Server.IIS", "Iis")]
    [InlineData("Microsoft.AspNetCore.Server.HttpSys", "HttpSys")]
    [InlineData("Microsoft.AspNetCore.TestHost", "Other")]
    public void Server_IsRecognisedByAssembly(string assembly, string expected) =>
        RequestLimitsServerSupport.Classify(assembly).ToString().Should().Be(expected);
}
