# Performance Verification

## Microbenchmarks

Run the BenchmarkDotNet suite:

```bash
dotnet run -c Release --project perf/ApiPipeline.NET.Perf
```

Current baseline scenarios:
- `MinimalPipeline_GetPing`
- `FullPipeline_GetPing`

Track and compare:
- Mean latency
- Allocated bytes/op
- Gen0/Gen1 collections

## Macro load test matrix

Use a repeatable external load generator (`bombardier`, `wrk`, or k6) against the sample app.

| Scenario | Purpose | Suggested threshold |
|---|---|---|
| Minimal pipeline | Baseline framework overhead | Reference only |
| Full pipeline (all core features) | Steady-state throughput and p95 | <= 20% p95 increase vs baseline |
| Full pipeline + OpenTelemetry | Observability overhead | <= 10% additional p95 vs full pipeline |
| CORS preflight heavy mix | CORS policy path stress | No allocation spikes across runs |

## CI gate recommendation

- Keep benchmark outputs as build artifacts.
- Fail CI only on sustained regressions (for example, 3 consecutive runs above threshold) to reduce noise.
