"""Stability and comparison rules for repeated BenchmarkDotNet runs."""

from __future__ import annotations

import hashlib
import json
import math
import shutil
import statistics
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Sequence

from scripts.performance.benchmark_results import BenchmarkRow


class TimingSummaryError(ValueError):
    """Raised when timing evidence cannot be safely summarized or promoted."""


@dataclass(frozen=True)
class MachineFingerprint:
    os: str
    architecture: str
    cpu_model: str
    dotnet_sdk: str
    dotnet_runtime: str
    docker_server: str
    git_revision: str
    benchmark_job: str

    def to_dict(self) -> dict[str, str]:
        return {
            "os": self.os,
            "architecture": self.architecture,
            "cpuModel": self.cpu_model,
            "dotnetSdk": self.dotnet_sdk,
            "dotnetRuntime": self.dotnet_runtime,
            "dockerServer": self.docker_server,
            "gitRevision": self.git_revision,
            "benchmarkJob": self.benchmark_job,
        }

    def machine_dict(self) -> dict[str, str]:
        """Return the stable host/toolchain identity, excluding the tested revision."""

        payload = self.to_dict()
        del payload["gitRevision"]
        return payload

    def same_machine(self, other: "MachineFingerprint") -> bool:
        return self.machine_dict() == other.machine_dict()

    @classmethod
    def from_dict(cls, payload: Mapping[str, object]) -> "MachineFingerprint":
        fields = {
            "os": "os",
            "architecture": "architecture",
            "cpu_model": "cpuModel",
            "dotnet_sdk": "dotnetSdk",
            "dotnet_runtime": "dotnetRuntime",
            "docker_server": "dockerServer",
            "git_revision": "gitRevision",
            "benchmark_job": "benchmarkJob",
        }
        values: dict[str, str] = {}
        for field, key in fields.items():
            value = payload.get(key)
            if not isinstance(value, str) or not value.strip():
                raise TimingSummaryError(f"fingerprint field {key!r} is missing")
            values[field] = value
        return cls(**values)

    @property
    def identifier(self) -> str:
        canonical = json.dumps(
            self.machine_dict(), sort_keys=True, separators=(",", ":")
        )
        return hashlib.sha256(canonical.encode("utf-8")).hexdigest()[:16]


@dataclass(frozen=True)
class TimingRun:
    name: str
    fingerprint: MachineFingerprint
    rows: tuple[BenchmarkRow, ...]


@dataclass(frozen=True)
class TimingRowSummary:
    key: str
    run_means_ns: tuple[float, ...]
    median_ns: float
    minimum_ns: float
    maximum_ns: float
    standard_deviation_ns: float
    cv: float
    allocation_min_bytes: float
    allocation_max_bytes: float
    eligible: bool
    reasons: tuple[str, ...]


@dataclass(frozen=True)
class TimingSummary:
    fingerprint: MachineFingerprint | None
    run_count: int
    rows: Mapping[str, TimingRowSummary]
    eligible: bool
    reasons: tuple[str, ...]


@dataclass(frozen=True)
class TimingComparisonRow:
    key: str
    baseline_median_ns: float | None
    candidate_median_ns: float | None
    change: float | None
    status: str


@dataclass(frozen=True)
class TimingComparison:
    rows: Mapping[str, TimingComparisonRow]
    enforce: bool

    @property
    def failed(self) -> bool:
        return self.enforce and any(
            row.status in {"regression", "ineligible"} for row in self.rows.values()
        )


def _unique_rows(run: TimingRun) -> dict[str, BenchmarkRow]:
    result: dict[str, BenchmarkRow] = {}
    for row in run.rows:
        if row.key in result:
            raise TimingSummaryError(f"{run.name}: duplicate benchmark row {row.key!r}")
        result[row.key] = row
    return result


def summarize_runs(
    runs: Sequence[TimingRun], *, expected_runs: int = 5, max_cv: float = 0.05
) -> TimingSummary:
    """Summarize repeated run means and determine timing eligibility."""

    if expected_runs <= 0:
        raise TimingSummaryError("expected_runs must be positive")
    if max_cv < 0:
        raise TimingSummaryError("max_cv cannot be negative")

    global_reasons: list[str] = []
    if len(runs) != expected_runs:
        global_reasons.append(
            f"timing eligibility requires exactly {expected_runs} successful runs; found {len(runs)}"
        )

    fingerprint = runs[0].fingerprint if runs else None
    if fingerprint is None:
        global_reasons.append("no machine fingerprint is available")
    elif any(run.fingerprint != fingerprint for run in runs[1:]):
        global_reasons.append("machine fingerprint mismatch across runs")
    elif fingerprint.benchmark_job != "Default":
        global_reasons.append(
            "timing eligibility requires the BenchmarkDotNet Default job; "
            f"found {fingerprint.benchmark_job!r}"
        )

    indexed = [_unique_rows(run) for run in runs]
    keys = sorted({key for run_rows in indexed for key in run_rows})
    summaries: dict[str, TimingRowSummary] = {}
    for key in keys:
        present = [run_rows[key] for run_rows in indexed if key in run_rows]
        means = tuple(row.mean_ns for row in present)
        allocations = tuple(row.allocated_bytes for row in present)
        reasons: list[str] = []
        if len(present) != len(runs):
            reasons.append(f"benchmark missing from {len(runs) - len(present)} run(s)")
        if len(present) != expected_runs:
            reasons.append(
                f"benchmark requires {expected_runs} measurements; found {len(present)}"
            )

        if means:
            median = statistics.median(means)
            minimum = min(means)
            maximum = max(means)
            standard_deviation = statistics.pstdev(means)
            arithmetic_mean = statistics.fmean(means)
            cv = 0.0 if arithmetic_mean == 0 and standard_deviation == 0 else (
                math.inf
                if arithmetic_mean == 0
                else standard_deviation / arithmetic_mean
            )
            allocation_min = min(allocations)
            allocation_max = max(allocations)
        else:
            median = minimum = maximum = standard_deviation = cv = math.nan
            allocation_min = allocation_max = math.nan
        if means and cv > max_cv:
            reasons.append(f"cross-run CV {cv:.2%} exceeds {max_cv:.2%}")

        row_eligible = not reasons and not global_reasons
        summaries[key] = TimingRowSummary(
            key=key,
            run_means_ns=means,
            median_ns=median,
            minimum_ns=minimum,
            maximum_ns=maximum,
            standard_deviation_ns=standard_deviation,
            cv=cv,
            allocation_min_bytes=allocation_min,
            allocation_max_bytes=allocation_max,
            eligible=row_eligible,
            reasons=tuple(reasons),
        )

    if not keys:
        global_reasons.append("no benchmark rows were found")
    eligible = not global_reasons and bool(summaries) and all(
        row.eligible for row in summaries.values()
    )
    return TimingSummary(
        fingerprint=fingerprint,
        run_count=len(runs),
        rows=summaries,
        eligible=eligible,
        reasons=tuple(global_reasons),
    )


def compare_summaries(
    baseline: TimingSummary,
    candidate: TimingSummary,
    *,
    max_regression: float = 0.10,
    enforce: bool = False,
) -> TimingComparison:
    """Compare eligible same-machine medians; enforcement is always explicit."""

    if max_regression < 0:
        raise TimingSummaryError("max_regression cannot be negative")
    same_machine = (
        baseline.fingerprint is not None
        and candidate.fingerprint is not None
        and baseline.fingerprint.same_machine(candidate.fingerprint)
    )
    rows: dict[str, TimingComparisonRow] = {}
    for key in sorted(set(baseline.rows) | set(candidate.rows)):
        old = baseline.rows.get(key)
        new = candidate.rows.get(key)
        if (
            old is None
            or new is None
            or not baseline.eligible
            or not candidate.eligible
            or not old.eligible
            or not new.eligible
            or not same_machine
        ):
            rows[key] = TimingComparisonRow(
                key,
                old.median_ns if old else None,
                new.median_ns if new else None,
                None,
                "ineligible",
            )
            continue
        change = (
            math.inf
            if old.median_ns == 0 and new.median_ns > 0
            else 0.0
            if old.median_ns == 0
            else new.median_ns / old.median_ns - 1.0
        )
        if change > max_regression:
            status = "regression" if enforce else "regression-informational"
        else:
            status = "pass" if enforce else "informational"
        rows[key] = TimingComparisonRow(
            key, old.median_ns, new.median_ns, change, status
        )
    return TimingComparison(rows, enforce)


def _summary_fingerprint(directory: Path) -> tuple[MachineFingerprint, bool]:
    summary_path = directory / "summary.json"
    try:
        payload = json.loads(summary_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise TimingSummaryError(f"cannot read {summary_path}: {error}") from error
    if not isinstance(payload, dict):
        raise TimingSummaryError(f"invalid timing summary: {summary_path}")
    fingerprint_payload = payload.get("fingerprint")
    if not isinstance(fingerprint_payload, dict):
        raise TimingSummaryError(f"missing fingerprint in {summary_path}")
    return MachineFingerprint.from_dict(fingerprint_payload), payload.get("eligible") is True


def promote_candidate(
    candidate: Path, accepted: Path, *, promote: bool
) -> None:
    """Copy eligible timing evidence only after explicit same-machine approval."""

    if not promote:
        raise TimingSummaryError("promotion requires explicit --promote")
    candidate = Path(candidate)
    accepted = Path(accepted)
    candidate_fingerprint, candidate_eligible = _summary_fingerprint(candidate)
    if candidate_fingerprint.benchmark_job != "Default":
        raise TimingSummaryError("cannot promote timing evidence from a non-Default job")
    if not candidate_eligible:
        raise TimingSummaryError("cannot promote an ineligible timing candidate")
    if accepted.exists():
        accepted_fingerprint, _ = _summary_fingerprint(accepted)
        if not accepted_fingerprint.same_machine(candidate_fingerprint):
            raise TimingSummaryError("candidate and accepted fingerprint mismatch")
    accepted.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(candidate, accepted, dirs_exist_ok=accepted.exists())
