# Security Policy

## Reporting a vulnerability

**Don't open a public issue, pull request or discussion.** Use GitHub's private reporting: on
[baps-apps/apipipeline-net](https://github.com/baps-apps/apipipeline-net), **Security** →
**Report a vulnerability**.

Include the package and version, the `ApiPipeline` configuration and `UseApiPipeline(...)` call
(secrets removed), the hosting setup (proxy, server, Redis version), and steps to reproduce with the
impact. The maintainers acknowledge, assess and agree a disclosure date with you. Fixes ship as a
patch of every package, with a GitHub advisory and a `### Security` entry in the
[CHANGELOG](CHANGELOG.md).

## Supported versions

| Version | Security fixes |
| --- | --- |
| 2.x | Yes |
| 1.x | No; [upgrade](docs/migration.md) |

## Scope

In scope: anything that weakens an API using the packages as documented, such as a bypass of proxy
trust, CORS or rate limits, a secure default that doesn't hold, a secret or personal data in logs,
metrics or Redis, or a documented configuration that turns out unsafe.

Out of scope: vulnerabilities in ASP.NET Core, StackExchange.Redis or other dependencies (report
them to their owners), and applications that disable a documented safeguard.

## Package IDs and dependency confusion

`ApiPipeline.NET` and `ApiPipeline.NET.*` are published only to the private feed and aren't reserved
on nuget.org. Defences:

- Consumers map both patterns to the private feed ([installation](README.md#installation)) and
  restore with lock files.
- `scripts/check-source-mapping.py` fails CI and releases unless every shipped ID resolves only to
  the private feed.
- Maintainers should publish an unlisted `0.0.1` placeholder for each ID on nuget.org from a team
  account, so nobody else can claim them.

Secure configuration: [production checklist](README.md#production-checklist).
