using ApiPipeline.NET.Performance;
using Testcontainers.Redis;

namespace ApiPipeline.NET.Perf;

internal static class RedisBenchmarkEnvironment
{
    public const string ConnectionStringVariable = "APIPIPELINE_PERF_REDIS_CONNECTION";
    private const string ParentMarkerVariable = "APIPIPELINE_PERF_REDIS_PARENT";

    public static async Task RunAsync(string[] args, Action<string[]> runBenchmarks)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runBenchmarks);

        if (IsBenchmarkChild(args)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ParentMarkerVariable)))
        {
            runBenchmarks(args);
            return;
        }

        var builder = new RedisBuilder(PerformanceImages.Redis);
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } dockerHost)
        {
            builder = builder.WithDockerEndpoint(dockerHost);
        }

        await using var container = builder.Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Docker is required to run ApiPipeline.NET performance benchmarks.", exception);
        }

        var previousConnection = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        var previousMarker = Environment.GetEnvironmentVariable(ParentMarkerVariable);
        Environment.SetEnvironmentVariable(ConnectionStringVariable, container.GetConnectionString());
        Environment.SetEnvironmentVariable(ParentMarkerVariable, Guid.NewGuid().ToString("N"));
        try
        {
            runBenchmarks(args);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, previousConnection);
            Environment.SetEnvironmentVariable(ParentMarkerVariable, previousMarker);
        }
    }

    private static bool IsBenchmarkChild(string[] args) =>
        args.Contains("--anonymousPipes", StringComparer.Ordinal);
}
