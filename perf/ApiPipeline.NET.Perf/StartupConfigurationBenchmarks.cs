using System.Globalization;
using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Perf;

public readonly record struct RequestLimitsSnapshot(
    bool AddServerHeader,
    long? MaxRequestBodySize,
    int MaxRequestHeadersTotalSize,
    int MaxRequestHeaderCount,
    TimeSpan RequestHeadersTimeout,
    long? MaxConcurrentConnections);

[MemoryDiagnoser]
public class RequestLimitsStartupBenchmarks
{
    private static readonly IConfiguration ActiveConfiguration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:RequestLimits:Enabled"] = "true",
            ["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "1048576",
            ["ApiPipeline:RequestLimits:MaxRequestHeadersTotalSize"] = "32768",
            ["ApiPipeline:RequestLimits:MaxRequestHeaderCount"] = "64",
            ["ApiPipeline:RequestLimits:RequestHeadersTimeout"] = "00:00:15",
            ["ApiPipeline:RequestLimits:MaxConcurrentConnections"] = "100",
        })
        .Build();

    [GlobalSetup]
    public void Setup()
    {
        var snapshot = Package_RequestLimits();
        BenchmarkPipeline.Require(snapshot == new RequestLimitsSnapshot(
            AddServerHeader: false,
            MaxRequestBodySize: 1_048_576,
            MaxRequestHeadersTotalSize: 32_768,
            MaxRequestHeaderCount: 64,
            RequestHeadersTimeout: TimeSpan.FromSeconds(15),
            MaxConcurrentConnections: 100),
            "request-limit startup options did not match the configured snapshot");
    }

    [Benchmark(Baseline = true)]
    public RequestLimitsSnapshot Baseline_FrameworkOptions()
    {
        var services = new ServiceCollection();
        services.AddOptions<KestrelServerOptions>();
        using var provider = services.BuildServiceProvider();
        return Snapshot(provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value);
    }

    [Benchmark]
    public RequestLimitsSnapshot Package_RequestLimits()
    {
        var services = new ServiceCollection();
        services.AddApiPipelineRequestLimits(ActiveConfiguration);
        using var provider = services.BuildServiceProvider();
        return Snapshot(provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value);
    }

    private static RequestLimitsSnapshot Snapshot(KestrelServerOptions options) => new(
        options.AddServerHeader,
        options.Limits.MaxRequestBodySize,
        options.Limits.MaxRequestHeadersTotalSize,
        options.Limits.MaxRequestHeaderCount,
        options.Limits.RequestHeadersTimeout,
        options.Limits.MaxConcurrentConnections);
}

public readonly record struct VersionDeprecationSnapshot(
    bool Enabled,
    bool ReportApiVersions,
    int VersionCount,
    string FirstVersion,
    string LastVersion,
    string PolicyBuilderType);

[MemoryDiagnoser]
public class VersionDeprecationStartupBenchmarks
{
    private const string DeprecationDate = "2030-01-01T00:00:00+00:00";
    private const string SunsetDate = "2031-01-01T00:00:00+00:00";

    private static readonly IConfiguration ActiveConfiguration = BuildConfiguration();

    [GlobalSetup]
    public void Setup() => ValidateConfiguredPolicies();

    [Benchmark(Baseline = true)]
    public VersionDeprecationSnapshot Baseline_AspVersioning()
    {
        var services = new ServiceCollection();
        services.AddApiVersioning(options => options.ReportApiVersions = true);
        using var provider = services.BuildServiceProvider();
        var versioning = provider.GetRequiredService<IOptions<ApiVersioningOptions>>().Value;
        return new VersionDeprecationSnapshot(
            Enabled: false,
            ReportApiVersions: versioning.ReportApiVersions,
            VersionCount: 0,
            FirstVersion: string.Empty,
            LastVersion: string.Empty,
            PolicyBuilderType: versioning.Policies.GetType().FullName ?? string.Empty);
    }

    [Benchmark]
    public VersionDeprecationSnapshot Package_DeprecationPolicies()
    {
        var services = new ServiceCollection();
        services.AddApiVersioning(options => options.ReportApiVersions = true);
        services.AddApiPipelineVersionDeprecation(ActiveConfiguration);
        using var provider = services.BuildServiceProvider();

        var package = provider.GetRequiredService<IOptions<ApiPipelineVersionDeprecationOptions>>().Value;
        var versioning = provider.GetRequiredService<IOptions<ApiVersioningOptions>>().Value;
        var entries = package.DeprecatedVersions;
        var snapshot = new VersionDeprecationSnapshot(
            Enabled: package.Enabled,
            ReportApiVersions: versioning.ReportApiVersions,
            VersionCount: entries.Count,
            FirstVersion: entries[0].Version,
            LastVersion: entries[^1].Version,
            PolicyBuilderType: versioning.Policies.GetType().FullName ?? string.Empty);
        return snapshot;
    }

    private static void ValidateConfiguredPolicies()
    {
        var services = new ServiceCollection();
        services.AddApiVersioning(options => options.ReportApiVersions = true);
        services.AddApiPipelineVersionDeprecation(ActiveConfiguration);
        using var provider = services.BuildServiceProvider();

        var package = provider.GetRequiredService<IOptions<ApiPipelineVersionDeprecationOptions>>().Value;
        var versioning = provider.GetRequiredService<IOptions<ApiVersioningOptions>>().Value;
        BenchmarkPipeline.Require(package.Enabled && versioning.ReportApiVersions,
            "version deprecation and ASP.NET API version reporting were not enabled");
        BenchmarkPipeline.Require(package.DeprecatedVersions.Count == 3,
            "version deprecation did not bind all three configured versions");

        var expectedDeprecation = DateTimeOffset.Parse(DeprecationDate);
        var expectedSunset = DateTimeOffset.Parse(SunsetDate);
        var deprecations = new DeprecationPolicyManager(
            Options.Create(versioning));
        var sunsets = new SunsetPolicyManager(
            Options.Create(versioning));
        for (var index = 0; index < package.DeprecatedVersions.Count; index++)
        {
            var entry = package.DeprecatedVersions[index];
            var versionNumber = index + 1;
            BenchmarkPipeline.Require(entry.Version == $"{versionNumber}.0"
                && entry.DeprecationDate == expectedDeprecation
                && entry.SunsetDate == expectedSunset,
                $"version deprecation entry {index} did not bind its version and dates");
            BenchmarkPipeline.Require(
                Uri.TryCreate(entry.DeprecationLink, UriKind.Absolute, out _)
                && Uri.TryCreate(entry.SunsetLink, UriKind.Absolute, out _),
                $"version deprecation entry {index} did not bind absolute links");
            BenchmarkPipeline.Require(ApiVersionParser.Default.TryParse(entry.Version, out var apiVersion)
                && apiVersion is not null,
                $"version deprecation entry {index} did not parse");
            BenchmarkPipeline.Require(
                deprecations.TryGetPolicy(string.Empty, apiVersion!, out var deprecationPolicy)
                && deprecationPolicy is not null,
                $"ASP.Versioning did not construct deprecation policy {entry.Version}");
            BenchmarkPipeline.Require(
                sunsets.TryGetPolicy(string.Empty, apiVersion!, out var sunsetPolicy)
                && sunsetPolicy is not null,
                $"ASP.Versioning did not construct sunset policy {entry.Version}");

            var context = new DefaultHttpContext();
            context.Response.WriteDeprecationPolicy(deprecationPolicy!);
            context.Response.WriteSunsetPolicy(sunsetPolicy!);
            var links = context.Response.Headers.Link.ToString();
            var deprecation = context.Response.Headers["Deprecation"].ToString();
            var sunset = context.Response.Headers["Sunset"].ToString();
            BenchmarkPipeline.Require(deprecation == $"@{expectedDeprecation.ToUnixTimeSeconds()}"
                && sunset == expectedSunset.ToString("R", CultureInfo.InvariantCulture)
                && links.Contains(entry.DeprecationLink!, StringComparison.Ordinal)
                && links.Contains(entry.SunsetLink!, StringComparison.Ordinal),
                $"ASP.Versioning policy {entry.Version} did not emit its dates and links");
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["ApiPipeline:VersionDeprecation:Enabled"] = "true",
        };

        for (var index = 0; index < 3; index++)
        {
            var version = index + 1;
            var prefix = $"ApiPipeline:VersionDeprecation:DeprecatedVersions:{index}";
            values[$"{prefix}:Version"] = $"{version}.0";
            values[$"{prefix}:DeprecationDate"] = DeprecationDate;
            values[$"{prefix}:SunsetDate"] = SunsetDate;
            values[$"{prefix}:DeprecationLink"] = $"https://docs.example.com/v{version}/deprecation";
            values[$"{prefix}:SunsetLink"] = $"https://docs.example.com/v{version}/sunset";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
