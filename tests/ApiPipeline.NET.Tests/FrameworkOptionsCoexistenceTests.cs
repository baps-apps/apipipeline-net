using FluentAssertions;
using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// An application that configures a framework options type itself (for example
/// <c>AddResponseCompression(o => ...)</c>) before calling the pipeline's registration must not
/// silently lose the pipeline's configuration of that same type. Both configurations apply, in
/// registration order.
/// </summary>
public sealed class FrameworkOptionsCoexistenceTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    /// <summary>
    /// Fails if the pipeline's compression configurator is registered with TryAdd, which skips it
    /// whenever the application registered any configurator for the type first.
    /// </summary>
    [Fact]
    public void ResponseCompression_PipelineConfiguration_Applies_After_ApplicationConfiguredTheTypeFirst()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResponseCompression(o => o.ExcludedMimeTypes = ["image/png"]);
        services.AddApiPipelineResponseCompression(Config(
            ("ApiPipeline:ResponseCompression:Enabled", "true"),
            ("ApiPipeline:ResponseCompression:EnableForHttps", "true"),
            ("ApiPipeline:ResponseCompression:EnableBrotli", "false")));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value;

        options.EnableForHttps.Should().BeTrue("the pipeline's EnableForHttps=true must still apply");
        options.Providers.Should().ContainSingle("only Gzip is enabled by the pipeline configuration");
        options.ExcludedMimeTypes.Should().Contain("image/png", "the application's own configuration must still apply");
    }

    /// <summary>
    /// Fails if the pipeline's caching configurator is registered with TryAdd.
    /// </summary>
    [Fact]
    public void ResponseCaching_PipelineConfiguration_Applies_After_ApplicationConfiguredTheTypeFirst()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResponseCaching(o => o.MaximumBodySize = 1234);
        services.AddApiPipelineResponseCaching(Config(
            ("ApiPipeline:ResponseCaching:Enabled", "true"),
            ("ApiPipeline:ResponseCaching:SizeLimitBytes", "4096")));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ResponseCachingOptions>>().Value;

        options.SizeLimit.Should().Be(4096, "the pipeline's SizeLimitBytes must still apply");
        options.MaximumBodySize.Should().Be(1234, "the application's own configuration must still apply");
    }
}
