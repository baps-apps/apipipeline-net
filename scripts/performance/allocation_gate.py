"""Managed-allocation budgets for BenchmarkDotNet result rows."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Optional, Sequence

from scripts.performance.benchmark_results import BenchmarkResultError, BenchmarkRow


@dataclass(frozen=True)
class AllocationBudget:
    accepted_bytes: float
    maximum_bytes: float
    reason: Optional[str] = None


@dataclass(frozen=True)
class AllocationBaseline:
    schema_version: int
    source_environment: str
    runtime: str
    rows: Mapping[str, AllocationBudget]


@dataclass(frozen=True)
class AllocationFinding:
    key: str
    accepted_bytes: Optional[float]
    current_bytes: Optional[float]
    maximum_bytes: Optional[float]
    delta_bytes: Optional[float]
    delta_percent: Optional[float]
    status: str
    reason: Optional[str] = None


@dataclass(frozen=True)
class AllocationReport:
    findings: tuple[AllocationFinding, ...]

    @property
    def passed(self) -> bool:
        return all(finding.status == "pass" for finding in self.findings)


def default_maximum_bytes(accepted_bytes: float) -> int:
    """Return the reviewed default ceiling: accepted + max(64 B, 5%)."""

    if accepted_bytes < 0 or not math.isfinite(accepted_bytes):
        raise BenchmarkResultError(
            f"accepted allocation must be a finite non-negative value: {accepted_bytes}"
        )
    return math.ceil(accepted_bytes + max(64.0, accepted_bytes * 0.05))


def _number(value: object, field: str, key: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BenchmarkResultError(f"baseline row {key!r}: {field} must be numeric")
    result = float(value)
    if result < 0 or not math.isfinite(result):
        raise BenchmarkResultError(
            f"baseline row {key!r}: {field} must be finite and non-negative"
        )
    return result


def load_baseline(path: Path) -> AllocationBaseline:
    """Load and validate allocation-baseline schema version 1."""

    try:
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise BenchmarkResultError(f"{path}: cannot read allocation baseline: {error}") from error

    if not isinstance(payload, dict) or payload.get("schemaVersion") != 1:
        raise BenchmarkResultError(f"{path}: unsupported allocation baseline schema")
    source_environment = payload.get("sourceEnvironment")
    runtime = payload.get("runtime")
    raw_rows = payload.get("rows")
    if not isinstance(source_environment, str) or not source_environment.strip():
        raise BenchmarkResultError(f"{path}: sourceEnvironment must be a non-empty string")
    if not isinstance(runtime, str) or not runtime.strip():
        raise BenchmarkResultError(f"{path}: runtime must be a non-empty string")
    if not isinstance(raw_rows, dict):
        raise BenchmarkResultError(f"{path}: rows must be an object")

    rows: dict[str, AllocationBudget] = {}
    for key, raw_budget in raw_rows.items():
        if not isinstance(key, str) or not key or not isinstance(raw_budget, dict):
            raise BenchmarkResultError(f"{path}: invalid allocation baseline row {key!r}")
        accepted = _number(raw_budget.get("acceptedBytes"), "acceptedBytes", key)
        maximum = _number(raw_budget.get("maximumBytes"), "maximumBytes", key)
        if maximum < accepted:
            raise BenchmarkResultError(
                f"baseline row {key!r}: maximumBytes cannot be below acceptedBytes"
            )
        reason = raw_budget.get("reason")
        if reason is not None and (not isinstance(reason, str) or not reason.strip()):
            raise BenchmarkResultError(
                f"baseline row {key!r}: reason must be a non-empty string"
            )
        rows[key] = AllocationBudget(accepted, maximum, reason)

    return AllocationBaseline(1, source_environment, runtime, rows)


def _unique_rows(rows: Sequence[BenchmarkRow]) -> dict[str, BenchmarkRow]:
    by_key: dict[str, BenchmarkRow] = {}
    for row in rows:
        if row.key in by_key:
            raise BenchmarkResultError(f"duplicate benchmark key {row.key!r}")
        by_key[row.key] = row
    return by_key


def check_allocations(
    rows: Sequence[BenchmarkRow], baseline: AllocationBaseline
) -> AllocationReport:
    """Compare a complete result row-set with a complete accepted row-set."""

    current = _unique_rows(rows)
    findings: list[AllocationFinding] = []
    for key in sorted(set(current) | set(baseline.rows)):
        row = current.get(key)
        budget = baseline.rows.get(key)
        if row is None:
            findings.append(
                AllocationFinding(
                    key,
                    budget.accepted_bytes if budget else None,
                    None,
                    budget.maximum_bytes if budget else None,
                    None,
                    None,
                    "missing",
                    budget.reason if budget else None,
                )
            )
            continue
        if budget is None:
            findings.append(
                AllocationFinding(
                    key,
                    None,
                    row.allocated_bytes,
                    None,
                    None,
                    None,
                    "unexpected",
                )
            )
            continue

        delta = row.allocated_bytes - budget.accepted_bytes
        if budget.accepted_bytes == 0:
            delta_percent = 0.0 if delta == 0 else math.inf
        else:
            delta_percent = delta / budget.accepted_bytes * 100.0
        status = "pass" if row.allocated_bytes <= budget.maximum_bytes else "over-budget"
        findings.append(
            AllocationFinding(
                key,
                budget.accepted_bytes,
                row.allocated_bytes,
                budget.maximum_bytes,
                delta,
                delta_percent,
                status,
                budget.reason,
            )
        )
    return AllocationReport(tuple(findings))


def write_candidate(
    rows: Sequence[BenchmarkRow],
    path: Path,
    *,
    source_environment: str,
    runtime: str,
    accepted_baseline: Optional[Path] = None,
    reasons: Optional[Mapping[str, str]] = None,
) -> None:
    """Write a new review candidate without mutating accepted evidence."""

    candidate = Path(path)
    if accepted_baseline is not None and candidate.resolve() == Path(accepted_baseline).resolve():
        raise BenchmarkResultError("candidate path must not be the accepted baseline")
    if candidate.exists():
        raise BenchmarkResultError(f"candidate already exists: {candidate}")

    current = _unique_rows(rows)
    reason_by_key = reasons or {}
    payload = {
        "schemaVersion": 1,
        "sourceEnvironment": source_environment,
        "runtime": runtime,
        "rows": {
            key: {
                "acceptedBytes": row.allocated_bytes,
                "maximumBytes": default_maximum_bytes(row.allocated_bytes),
                **(
                    {"reason": reason_by_key[key]}
                    if key in reason_by_key
                    else {}
                ),
            }
            for key, row in sorted(current.items())
        },
    }
    candidate.parent.mkdir(parents=True, exist_ok=True)
    try:
        with candidate.open("x", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2, sort_keys=False)
            handle.write("\n")
    except OSError as error:
        raise BenchmarkResultError(f"cannot write candidate {candidate}: {error}") from error
