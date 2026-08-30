using ApiPipeline.NET.Pipeline;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Perf;

[MemoryDiagnoser]
public class RequestTimeoutBenchmarks
{
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _timeouts = null!;
    private int _baselineCalls;
    private int _timeoutCalls;

    [GlobalSetup]
    public async Task Setup()
    {
        var configuration = new ConfigurationBuilder().Build();
        _services = BenchmarkPipeline.BuildProvider(configuration, static (services, _) =>
            services.AddApiPipelineRequestTimeouts(options =>
            {
                options.Enabled = true;
                options.DefaultTimeout = TimeSpan.FromSeconds(30);
            }));

        _baseline = BenchmarkPipeline.BuildDelegate(
            _services,
            static app => app.UseRouting(),
            context =>
            {
                Interlocked.Increment(ref _baselineCalls);
                return Task.CompletedTask;
            });
        _timeouts = BenchmarkPipeline.BuildDelegate(
            _services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts()),
            context =>
            {
                Interlocked.Increment(ref _timeoutCalls);
                return Task.CompletedTask;
            });
        BenchmarkPipeline.StartHostedServices(_services);

        var baselineValidated = false;
        var timeoutValidated = false;
        var validationBaseline = BenchmarkPipeline.BuildDelegate(
            _services,
            static app => app.UseRouting(),
            context =>
            {
                baselineValidated = true;
                BenchmarkPipeline.Require(
                    context.Features.Get<IHttpRequestTimeoutFeature>() is null,
                    "the no-timeout baseline exposed IHttpRequestTimeoutFeature");
                return Task.CompletedTask;
            });
        var validationTimeouts = BenchmarkPipeline.BuildDelegate(
            _services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithRequestTimeouts()),
            context =>
            {
                timeoutValidated = true;
                BenchmarkPipeline.Require(
                    context.Features.Get<IHttpRequestTimeoutFeature>() is not null,
                    "request-timeout middleware did not expose IHttpRequestTimeoutFeature");
                BenchmarkPipeline.Require(context.RequestAborted.CanBeCanceled,
                    "request-timeout middleware did not expose a cancellable request token");
                return Task.CompletedTask;
            });
        await validationBaseline(CreateContext());
        await validationTimeouts(CreateContext());
        BenchmarkPipeline.Require(baselineValidated && timeoutValidated,
            "timeout semantic validation did not exercise both terminals");

        await _baseline(CreateContext());
        await _timeouts(CreateContext());
        BenchmarkPipeline.Require(_baselineCalls == 1 && _timeoutCalls == 1,
            "timeout setup did not exercise both pass-through terminals");
        _baselineCalls = 0;
        _timeoutCalls = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkPipeline.Require(_baselineCalls + _timeoutCalls > 0,
            "timeout benchmark process did not exercise its measured path");
        _services.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task Baseline_NoTimeoutFeature() => _baseline(CreateContext());

    [Benchmark]
    public Task RequestTimeout_Enabled() => _timeouts(CreateContext());

    private DefaultHttpContext CreateContext() =>
        BenchmarkPipeline.CreateContext(_services, HttpMethods.Get, "/timeout", "https");
}
