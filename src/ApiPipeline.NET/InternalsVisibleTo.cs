using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ApiPipeline.NET.Tests")]

// The benchmarks measure single middlewares, partition keys and metrics in isolation.
[assembly: InternalsVisibleTo("ApiPipeline.NET.Perf")]
