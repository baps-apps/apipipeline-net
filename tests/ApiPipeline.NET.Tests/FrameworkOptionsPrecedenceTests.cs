using System.IO.Compression;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The pipeline's framework settings are defaults: the application's own, registered before or
/// after, win; the pipeline's apply where the application set nothing.
/// </summary>
public sealed class FrameworkOptionsPrecedenceTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static T Resolve<T>(IServiceCollection services)
        where T : class
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    /// <summary>
    /// Kestrel does not bind <c>Kestrel:Limits</c> or <c>Kestrel:AddServerHeader</c> itself; an
    /// application that binds the section first keeps its values.
    /// </summary>
    [Fact]
    public async Task Kestrel_SectionBoundByApplicationFirst_WinsOverRequestLimits()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:RequestLimits:Enabled"] = "true",
            ["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "1000",
            ["ApiPipeline:RequestLimits:MaxRequestHeaderCount"] = "50",
            ["Kestrel:Limits:MaxRequestBodySize"] = "2000",
            ["Kestrel:AddServerHeader"] = "true",
        });
        builder.Services.Configure<KestrelServerOptions>(builder.Configuration.GetSection("Kestrel"));
        builder.Services.AddApiPipelineRequestLimits(builder.Configuration);

        await using var app = builder.Build();
        var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        kestrel.Limits.MaxRequestBodySize.Should().Be(2000, "Kestrel:Limits is the application's own setting");
        kestrel.AddServerHeader.Should().BeTrue("Kestrel:AddServerHeader is the application's own setting");
        kestrel.Limits.MaxRequestHeaderCount.Should().Be(50, "the application set nothing for it, so the pipeline's value applies");
    }

    [Fact]
    public void Kestrel_NothingSetByApplication_PipelineValuesApply()
    {
        var services = Services();
        services.AddApiPipelineRequestLimits(Config(
            ("ApiPipeline:RequestLimits:Enabled", "true"),
            ("ApiPipeline:RequestLimits:MaxRequestBodySize", "1000")));

        var kestrel = Resolve<KestrelServerOptions>(services);

        kestrel.Limits.MaxRequestBodySize.Should().Be(1000);
        kestrel.AddServerHeader.Should().BeFalse("SuppressServerHeader defaults to true");
    }

    [Fact]
    public void Hsts_AddHstsBeforePipeline_Wins()
    {
        var services = Services();
        services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(1));
        services.AddApiPipelineSecurityHeaders(Config());

        var hsts = Resolve<HstsOptions>(services);

        hsts.MaxAge.Should().Be(TimeSpan.FromDays(1));
        hsts.IncludeSubDomains.Should().BeTrue("the application set nothing for it, so the pipeline's value applies");
    }

    [Fact]
    public void Hsts_AddHstsAfterPipeline_Wins()
    {
        var services = Services();
        services.AddApiPipelineSecurityHeaders(Config());
        services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(1));

        Resolve<HstsOptions>(services).MaxAge.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public void Hsts_NothingSetByApplication_PipelineValuesApply()
    {
        var services = Services();
        services.AddApiPipelineSecurityHeaders(Config());

        Resolve<HstsOptions>(services).MaxAge.Should().Be(TimeSpan.FromDays(365));
    }

    [Fact]
    public void ResponseCompression_ApplicationSettingBeforePipeline_Wins()
    {
        var services = Services();
        services.AddResponseCompression(o => o.EnableForHttps = false);
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
        services.AddApiPipelineResponseCompression(Config(
            ("ApiPipeline:ResponseCompression:Enabled", "true"),
            ("ApiPipeline:ResponseCompression:EnableForHttps", "true")));

        Resolve<ResponseCompressionOptions>(services).EnableForHttps.Should().BeFalse();
        Resolve<BrotliCompressionProviderOptions>(services).Level.Should().Be(CompressionLevel.Optimal);
        Resolve<GzipCompressionProviderOptions>(services).Level.Should().Be(
            CompressionLevel.Fastest, "the application set nothing for Gzip, so the pipeline's level applies");
    }

    [Fact]
    public void OutputCaching_ApplicationSettingBeforePipeline_Wins()
    {
        var services = Services();
        services.AddOutputCache(o => o.UseCaseSensitivePaths = true);
        services.AddApiPipelineOutputCaching(Config(
            ("ApiPipeline:OutputCaching:Enabled", "true"),
            ("ApiPipeline:OutputCaching:SizeLimitBytes", "4096")));

        var options = Resolve<OutputCacheOptions>(services);

        options.UseCaseSensitivePaths.Should().BeTrue();
        options.SizeLimit.Should().Be(4096, "the application set nothing for it, so the pipeline's value applies");
    }

    [Fact]
    public void RequestTimeouts_ApplicationDefaultPolicyBeforePipeline_Wins()
    {
        var services = Services();
        services.AddRequestTimeouts(o => o.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(5) });
        services.AddApiPipelineRequestTimeouts(Config(("ApiPipeline:RequestTimeouts:Enabled", "true")));

        Resolve<RequestTimeoutOptions>(services).DefaultPolicy!.Timeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Forms_ApplicationSettingBeforePipeline_Wins()
    {
        var services = Services();
        services.Configure<FormOptions>(o => o.ValueCountLimit = 10);
        services.AddApiPipelineRequestLimits(Config(
            ("ApiPipeline:RequestLimits:Enabled", "true"),
            ("ApiPipeline:RequestLimits:MaxFormValueCount", "500"),
            ("ApiPipeline:RequestLimits:MaxRequestBodySize", "1000")));

        var form = Resolve<FormOptions>(services);

        form.ValueCountLimit.Should().Be(10);
        form.MultipartBodyLengthLimit.Should().Be(1000, "the application set nothing for it, so the pipeline's value applies");
    }
}
