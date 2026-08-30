using ApiPipeline.NET.Pipeline;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPipeline.NET.Perf;

[MemoryDiagnoser]
public class ResponseCompressionBenchmarks
{
    private const int PayloadSize = 4 * 1024;
    private static readonly byte[] Payload = Enumerable.Repeat((byte)'A', PayloadSize).ToArray();

    private readonly CountingWriteStream _baselineSink = new();
    private readonly CountingWriteStream _gzipSink = new();
    private ServiceProvider _services = null!;
    private RequestDelegate _baseline = null!;
    private RequestDelegate _gzip = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var configuration = new ConfigurationBuilder().Build();
        _services = BenchmarkPipeline.BuildProvider(configuration, static (services, _) =>
            services.AddApiPipelineResponseCompression(options =>
            {
                options.Enabled = true;
                options.EnableForHttps = true;
                options.EnableBrotli = false;
                options.EnableGzip = true;
            }));

        _baseline = BenchmarkPipeline.BuildDelegate(
            _services,
            static app => app.UseRouting(),
            WritePayloadAsync);
        _gzip = BenchmarkPipeline.BuildDelegate(
            _services,
            static app => app.UseApiPipeline(pipeline => pipeline.WithResponseCompression()),
            WritePayloadAsync);
        BenchmarkPipeline.StartHostedServices(_services);

        var baselineContext = CreateContext(_baselineSink);
        await _baseline(baselineContext);
        var baselineBytes = _baselineSink.BytesWritten;

        var gzipContext = CreateContext(_gzipSink);
        await _gzip(gzipContext);
        var gzipBytes = _gzipSink.BytesWritten;

        BenchmarkPipeline.Require(baselineBytes == PayloadSize,
            $"uncompressed payload wrote {baselineBytes} bytes instead of {PayloadSize}");
        BenchmarkPipeline.Require(gzipContext.Response.Headers.ContentEncoding == "gzip",
            "gzip response did not emit Content-Encoding: gzip");
        BenchmarkPipeline.Require(gzipBytes > 0 && gzipBytes < baselineBytes,
            $"gzip emitted {gzipBytes} bytes for a {baselineBytes}-byte baseline");
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true)]
    public Task Baseline_Uncompressed4KB() => _baseline(CreateContext(_baselineSink));

    [Benchmark]
    public Task Gzip_4KB() => _gzip(CreateContext(_gzipSink));

    private DefaultHttpContext CreateContext(CountingWriteStream sink)
    {
        sink.Reset();
        var context = BenchmarkPipeline.CreateContext(_services, HttpMethods.Get, "/compression", "https");
        context.Request.Headers.AcceptEncoding = "gzip";
        context.Response.Body = sink;
        return context;
    }

    private static Task WritePayloadAsync(HttpContext context)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.Body.WriteAsync(Payload, 0, Payload.Length);
    }
}
