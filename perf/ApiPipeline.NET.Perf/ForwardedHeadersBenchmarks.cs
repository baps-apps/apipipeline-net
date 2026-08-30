using System.Net;
using ApiPipeline.NET.Pipeline;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Perf;

internal static class ForwardedHeadersBenchmarkPipeline
{
    public static readonly IPAddress Loopback = IPAddress.Loopback;
    public static readonly IPAddress ForwardedClient = IPAddress.Parse("203.0.113.42");

    public static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = BenchmarkPipeline.BuildProvider(configuration, static (collection, _) =>
            collection.AddApiPipelineForwardedHeaders(options =>
            {
                options.Enabled = true;
                options.ClearDefaultProxies = true;
                options.KnownProxies = ["127.0.0.1"];
            }));
        BenchmarkPipeline.StartHostedServices(services);
        return services;
    }

    public static RequestDelegate BuildBaseline(IServiceProvider services) =>
        BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseRouting(),
            static _ => Task.CompletedTask);

    public static RequestDelegate BuildForwarded(IServiceProvider services) =>
        BenchmarkPipeline.BuildDelegate(
            services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithForwardedHeaders()),
            static _ => Task.CompletedTask);

    public static DefaultHttpContext CreateContext(IServiceProvider services, bool withHeaders)
    {
        var context = BenchmarkPipeline.CreateContext(services, HttpMethods.Get, "/forwarded", "http");
        context.Connection.RemoteIpAddress = Loopback;
        if (withHeaders)
        {
            context.Request.Headers["X-Forwarded-For"] = ForwardedClient.ToString();
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        }

        return context;
    }
}

[MemoryDiagnoser]
public class ForwardedHeadersBenchmarks
{
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _forwarded = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = ForwardedHeadersBenchmarkPipeline.BuildProvider();
        _baseline = ForwardedHeadersBenchmarkPipeline.BuildBaseline(_services);
        _forwarded = ForwardedHeadersBenchmarkPipeline.BuildForwarded(_services);

        var untouched = CreateContext();
        await _forwarded(untouched);
        BenchmarkPipeline.Require(
            untouched.Connection.RemoteIpAddress!.Equals(ForwardedHeadersBenchmarkPipeline.Loopback),
            "forwarded-header pass-through changed the peer address");
        BenchmarkPipeline.Require(untouched.Request.Scheme == "http",
            "forwarded-header pass-through changed the request scheme");
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true)]
    public Task Baseline_NoMiddleware() => _baseline(CreateContext());

    [Benchmark]
    public Task ForwardedHeaders_NoHeaders() => _forwarded(CreateContext());

    private DefaultHttpContext CreateContext() =>
        ForwardedHeadersBenchmarkPipeline.CreateContext(_services, withHeaders: false);
}

[MemoryDiagnoser]
public class ForwardedHeadersAppliedBenchmarks
{
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _forwarded = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = ForwardedHeadersBenchmarkPipeline.BuildProvider();
        _baseline = ForwardedHeadersBenchmarkPipeline.BuildBaseline(_services);
        _forwarded = ForwardedHeadersBenchmarkPipeline.BuildForwarded(_services);

        var applied = CreateContext();
        await _forwarded(applied);
        BenchmarkPipeline.Require(
            applied.Connection.RemoteIpAddress!.Equals(ForwardedHeadersBenchmarkPipeline.ForwardedClient),
            "X-Forwarded-For was not applied from the trusted loopback proxy");
        BenchmarkPipeline.Require(applied.Request.Scheme == "https",
            "X-Forwarded-Proto was not applied from the trusted loopback proxy");
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true)]
    public Task Baseline_Headers_NoMiddleware() => _baseline(CreateContext());

    [Benchmark]
    public Task ForwardedHeaders_Applied() => _forwarded(CreateContext());

    private DefaultHttpContext CreateContext() =>
        ForwardedHeadersBenchmarkPipeline.CreateContext(_services, withHeaders: true);
}
