# Contributing to ApiPipeline.NET

Report vulnerabilities privately per [SECURITY.md](SECURITY.md), not in an issue.

## Prerequisites

- The .NET SDK in `global.json` (10.0.404 or a later 10.0.4xx patch). CI uses the same. The pin is a
  security floor: don't lower it to match an older local SDK.
- Microsoft's build of that SDK. Source-built SDKs (Homebrew's `dotnet`, some Linux distribution
  packages) can't build this repository: their IIS and HTTP.sys assemblies are empty stubs, so the
  build fails with CS0246. To install the pinned SDK into a folder without touching the system one:

  ```bash
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
  bash dotnet-install.sh --version 10.0.404 --install-dir "$HOME/.dotnet-10.0.404" --no-path
  export DOTNET_ROOT="$HOME/.dotnet-10.0.404" PATH="$HOME/.dotnet-10.0.404:$PATH"
  ```

- Docker, for the Redis tests. Without it they are skipped locally and fail on CI (`CI=true`).
- For `scripts/aot-smoke.sh`: clang and zlib (Linux) or Xcode command line tools (macOS), and Docker
  for its shared (Redis) rate limiting check, which is skipped locally without it and fails on CI.

## Getting started

```bash
git clone https://github.com/baps-apps/apipipeline-net.git
cd apipipeline-net
dotnet build
```

Restore needs the `baps-apps-packages` feed: put a PAT with `read:packages` in your user-level
NuGet config as credentials only ([README](README.md#installation)). Never run
`dotnet nuget update source` here without `--configfile`: it writes the token into the committed
`nuget.config`.

The feed provides `CodeStyle.NET` and the previous release of each package, which package
validation compares the public API against (`ApiCompatBaselineVersion` in `Directory.Build.targets`;
intended breaks in each `CompatibilitySuppressions.xml`). Offline with a warm cache,
`-p:SkipApiCompatBaseline=true` skips only that comparison; CI never does.

`nuget.config` maps `ApiPipeline.NET` and `ApiPipeline.NET.*` to the private feed only;
`scripts/check-source-mapping.py` fails CI if any shipped package ID could resolve elsewhere. New
package IDs must start with `ApiPipeline.NET.`.

## Running tests

xUnit v3 on Microsoft.Testing.Platform: use `--project`/`--solution` and `--filter-class`,
`--filter-method` or `--filter-query`.

```bash
dotnet test --solution ApiPipeline.NET.slnx
dotnet test --project tests/ApiPipeline.NET.Tests --filter-class "*CorsTests"

# Without Docker-dependent tests
dotnet test --solution ApiPipeline.NET.slnx --filter-not-trait "Category=Docker" --ignore-exit-code 8
```

CI enforces line coverage of 85% (`ApiPipeline.NET`), 75% (`Versioning`) and 80% (`Redis`) in
`.github/workflows/ci.yml`. Raise thresholds as coverage grows; never lower them to land a change.

Writing tests:

- Record metrics with `MetricRecorder` over the host's `IMeterFactory`: tests run in parallel.
- Never assert wall-clock durations. Use `FakeTimeProvider` and events, not sleeps.
- The Redis test project is serial: it pauses the shared container and runs on its clock.

## Docs

```bash
python3 -I scripts/check-doc-links.py
```

Each fact lives in one place: feature options, behaviour and troubleshooting in
`docs/features/`, design in `docs/ARCHITECTURE.md`. Link instead of repeating. Keep docs brief.

## Dependencies

Versions are in `Directory.Packages.props` (central package management with transitive pinning).
Lock files are committed and CI restores in locked mode; after a version change run
`dotnet restore ApiPipeline.NET.slnx --force-evaluate` and commit the lock files.

Pins Dependabot can't see; update by hand:

- `ApiPipelineILLinkPackVersion` in `Directory.Build.targets` (then regenerate lock files).
- Redis test images in `tests/ApiPipeline.NET.RateLimiting.Redis.Tests/TestImages.cs`.
- Performance images in `perf/PerformanceImages.cs` and `perf/load/k6-image.txt` (then run the
  performance smoke commands in [docs/performance.md](docs/performance.md)).

## Repository structure

```text
src/ApiPipeline.NET/                     Core package
src/ApiPipeline.NET.Versioning/          Asp.Versioning deprecation policies
src/ApiPipeline.NET.RateLimiting.Redis/  Rate limits shared through Redis
src/Shared/                              Source compiled into every package
samples/ApiPipeline.NET.Sample/          Reference consumer
tests/                                   Unit, integration, Redis and AOT smoke tests
perf/                                    Benchmarks and load tests
docs/                                    Guides; docs/features/ per feature
```

## Coding guidelines

- `net10.0`, nullable enabled, no unnecessary dependencies.
- Settings come from validated options, read once at startup.
- Public API: an XML `<summary>` of a sentence or two (what, default, limits).
- Comments only where code can't say why, one line where possible.
- No history in code or docs; it belongs in `CHANGELOG.md` and commit messages.

## PR checklist

- [ ] `dotnet build` clean; `dotnet test` passes with Docker running
- [ ] Tests for every behaviour change
- [ ] Docs and `CHANGELOG.md` updated
- [ ] No secrets or local artifacts
- [ ] Performance changes: allocation gate passes; timing claims use five stable runs
- [ ] Target framework change: [checklist](#upgrading-the-target-framework) done

## Upgrading the target framework

The package relies on these ASP.NET Core behaviours. Re-check each on every .NET major, ideally on
the first preview:

- [ ] Routing skips a request whose endpoint is already set (`PipelineRoutingTests`).
- [ ] `ForwardedHeadersMiddleware` trusts every peer when both trust lists are empty (the reason for
      the trust guard).
- [ ] The partitioned limiter creates each partition's limiter once and caches it
      (`RateLimitPolicySignature`).
- [ ] `FixedWindowRateLimiter` throws on `PermitLimit = 0` (`DenyAllRateLimiter`).
- [ ] `GlobalLimiter` runs in addition to the endpoint policy (`ConfigureRateLimiterOptions` uses
      `GetNoLimiter` for endpoints with their own).
- [ ] `OptionsMonitor` throws from its reload callback on invalid configuration; a polling file
      watcher must survive it (`PollingFileWatcherConfigurationTests`).
- [ ] `CorsService` wildcard and `IsOriginAllowed` behaviour.
- [ ] `HstsMiddleware`/`HstsOptions`, including default `ExcludedHosts`
      ([architecture](docs/ARCHITECTURE.md#hsts-is-delegated-to-hstsmiddleware), `HstsDelegationTests`).
- [ ] The options and configuration-binding source generators (AOT build).

## Releasing

[Semantic Versioning](docs/VERSIONING.md). Tag the commit whose `<Version>` in
`src/Shared/Shared.props` is the release (`v2.0.0`); the release workflow fails on a mismatch.

After the release, `scripts/check-api-baseline.py` fails CI until you:

1. Bump `<Version>` to the next version.
2. Set `ApiCompatBaselineVersion` to the version released, and regenerate each
   `CompatibilitySuppressions.xml` with
   `dotnet pack <project> -c Release -p:GenerateCompatibilitySuppressionFile=true`.

## License

Proprietary, internal use only ([LICENSE](LICENSE)). Contributions fall under the same terms. Do not
copy the source or packages outside the organization.
