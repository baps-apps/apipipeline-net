using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Perf;

internal static class BenchmarkPipeline
{
    public static ServiceProvider BuildProvider(
        IConfiguration configuration,
        Action<IServiceCollection, IConfiguration> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new BenchmarkEnvironment());
        services.AddSingleton(new DiagnosticListener("Microsoft.AspNetCore"));
        services.AddSingleton<DiagnosticSource>(provider => provider.GetRequiredService<DiagnosticListener>());
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Critical);
        });
        configure(services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public static RequestDelegate BuildDelegate(
        IServiceProvider services,
        Action<IApplicationBuilder> configure,
        RequestDelegate terminal)
    {
        var app = new ApplicationBuilder(services);
        configure(app);
        app.Run(terminal);
        return app.Build();
    }

    public static DefaultHttpContext CreateContext(
        IServiceProvider services,
        string method,
        PathString path,
        string scheme)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("benchmark.local");
        context.Features.Set<IHttpResponseFeature>(new BenchmarkResponseFeature());
        return context;
    }

    public static Task StartResponseAsync(HttpContext context) =>
        context.Features.GetRequiredFeature<IHttpResponseFeature>() is BenchmarkResponseFeature feature
            ? feature.StartAsync()
            : throw new InvalidOperationException("The benchmark response feature is missing.");

    /// <summary>
    /// Starts the hosted services the way the host does: every <c>StartingAsync</c> (where the
    /// package's startup checks run), then every <c>StartAsync</c>.
    /// </summary>
    public static void StartHostedServices(IServiceProvider services)
    {
        var hostedServices = services.GetServices<IHostedService>().ToList();
        foreach (var lifecycle in hostedServices.OfType<IHostedLifecycleService>())
        {
            lifecycle.StartingAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        foreach (var hostedService in hostedServices)
        {
            hostedService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Benchmark semantic guard failed: {message}");
        }
    }

    private sealed class BenchmarkEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "ApiPipeline.NET.Perf";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class BenchmarkResponseFeature : IHttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _starting = [];
        private readonly Stack<(Func<object, Task> Callback, object State)> _completed = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) =>
            _starting.Push((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state) =>
            _completed.Push((callback, state));

        public async Task StartAsync()
        {
            if (HasStarted)
            {
                return;
            }

            HasStarted = true;
            while (_starting.TryPop(out var registration))
            {
                await registration.Callback(registration.State).ConfigureAwait(false);
            }
        }
    }
}
