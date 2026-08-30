# Archived reviews

Point-in-time audits of this repository, kept for the reasoning behind decisions that are now in the
code. **None of them describes the package as it stands today.** Each one references files, options,
and metrics that have since changed or been deleted, and each was written before the follow-up work
it prompted.

They live here rather than in `docs/` because a review sitting alongside current documentation gets
read as current documentation, which is worse than not having it. Each one leads with the current
status of its findings, so it is clear up front what is history and what still stands — but the body
of each is written in the present tense of the day it was written, and should be read that way.

For how the package behaves now, read [docs/ARCHITECTURE.md](../ARCHITECTURE.md),
[docs/OBSERVABILITY.md](../OBSERVABILITY.md), and the per-feature docs in
[docs/features/](../features/). For what changed and why, read [CHANGELOG.md](../../CHANGELOG.md).

| Review | Date | Prompted |
|---|---|---|
| [architecture-review.md](architecture-review.md) | 2026-04-06 | The v1.1 hardening work: forwarded-headers trust guard, rate-limit partitioning, phase ordering. |
| [production-readiness-review-2026-09-02.md](production-readiness-review-2026-09-02.md) | 2026-09-02 | The v2.0.0 production-readiness follow-up: `ValidatedSnapshot<T>`, fail-closed rate limiting, one CIDR parser, the CORS and `ProcessHost` startup guards, API compatibility validation. Its **Resolution** section records the current status of every finding, and what was done for each. |

## 2.0.0 development notes

[changelog-2.0.0-development-notes.md](changelog-2.0.0-development-notes.md) is the detailed log
written while 2.0.0 was developed, moved out of `CHANGELOG.md` so the changelog stays user-facing.

## Design specs and implementation plans

[superpowers/](superpowers/) holds the design specs and step-by-step implementation plans written
before each change. They are records of intent at the time, not documentation: file names, option
names and test names in them predate later refactors (the 2.0.0 option-type renames, for one).

| Document | Date | Covers |
|---|---|---|
| [specs/2026-04-06-apipipeline-improvements-design.md](superpowers/specs/2026-04-06-apipipeline-improvements-design.md) | 2026-04-06 | Design for the v1.1 hardening. |
| [specs/2026-04-06-apipipeline-improvements-changes.md](superpowers/specs/2026-04-06-apipipeline-improvements-changes.md) | 2026-04-06 | The change list derived from it. |
| [plans/2026-04-06-apipipeline-hardening.md](superpowers/plans/2026-04-06-apipipeline-hardening.md) | 2026-04-06 | Implementation plan, hardening. |
| [plans/2026-04-06-apipipeline-production-hardening.md](superpowers/plans/2026-04-06-apipipeline-production-hardening.md) | 2026-04-06 | Implementation plan, production hardening. |
| [specs/2026-09-01-version-deprecation-delegation-design.md](superpowers/specs/2026-09-01-version-deprecation-delegation-design.md) | 2026-09-01 | Delegating version deprecation to Asp.Versioning. |
| [plans/2026-09-01-version-deprecation-delegation.md](superpowers/plans/2026-09-01-version-deprecation-delegation.md) | 2026-09-01 | Its implementation plan. |
