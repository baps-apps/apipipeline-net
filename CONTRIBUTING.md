# Contributing to ApiPipeline.NET

Thank you for contributing. This guide covers local setup, expectations, and PR quality gates.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Git
- C# editor (Visual Studio, Rider, VS Code + C# tools)

## Getting started

```bash
git clone https://github.com/baps-apps/apipipeline-net.git
cd apipipeline-net
dotnet restore
dotnet build
```

## Running tests

```bash
# Main test project
dotnet test tests/ApiPipeline.NET.Tests

# Full solution
dotnet test
```

## Repository structure

```text
src/
  ApiPipeline.NET/                 Core package
  ApiPipeline.NET.OpenTelemetry/   Optional observability package
  ApiPipeline.NET.Versioning/      Optional Asp.Versioning integration
samples/
  ApiPipeline.NET.Sample/          Reference consumer app
tests/
  ApiPipeline.NET.Tests/           Test suite
docs/
  features/                        Per-feature detailed documentation
  ARCHITECTURE.md, INTERNALS.md    Design and implementation docs
  OPERATIONS.md, RUNBOOK.md        Operational and incident docs
  TROUBLESHOOTING.md               Diagnostics guide
  VERSIONING.md                    Versioning policy
perf/
  ApiPipeline.NET.Perf/            BenchmarkDotNet scenarios
```

## Making changes

1. Branch from `main`.
2. Add or update tests with every behavior change.
3. Keep changes focused (one logical concern per commit).
4. Update docs for any user-visible behavior or config change.
5. Run `dotnet test` before opening a PR.

## Coding guidelines

- Target `net10.0`.
- Keep nullable reference types enabled.
- Prefer configuration-backed options over hardcoded values.
- Keep middleware behavior deterministic and side-effect minimal.
- Avoid unnecessary dependencies; use central package management via `Directory.Packages.props`.

## PR checklist

- [ ] Builds cleanly (`dotnet build`)
- [ ] Tests pass (`dotnet test`)
- [ ] Public API changes include XML docs where relevant
- [ ] `README.md` and relevant files in `docs/` are updated
- [ ] `CHANGELOG.md` has an entry under the current release section
- [ ] No secrets or local machine artifacts are committed

## Versioning

This project follows [Semantic Versioning](https://semver.org/). See [docs/VERSIONING.md](docs/VERSIONING.md).

## License

By contributing, you agree your contributions are licensed under [MIT](LICENSE).
