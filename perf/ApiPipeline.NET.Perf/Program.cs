using ApiPipeline.NET.Perf;
using BenchmarkDotNet.Running;

if (args is ["--runtime-version"])
{
    Console.WriteLine(Environment.Version);
    return;
}

await RedisBenchmarkEnvironment.RunAsync(
    args,
    benchmarkArgs => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(benchmarkArgs));
