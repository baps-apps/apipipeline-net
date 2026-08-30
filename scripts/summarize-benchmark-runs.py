#!/usr/bin/env python3
"""Summarize repeated BenchmarkDotNet artifacts and optional same-machine comparison."""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from scripts.performance.benchmark_results import (  # noqa: E402
    BenchmarkResultError,
    parse_reports,
)
from scripts.performance.timing_summary import (  # noqa: E402
    MachineFingerprint,
    TimingRun,
    TimingSummary,
    TimingSummaryError,
    compare_summaries,
    promote_candidate,
    summarize_runs,
)


def _load_fingerprint(path: Path) -> MachineFingerprint:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise TimingSummaryError(f"cannot read fingerprint {path}: {error}") from error
    if not isinstance(payload, dict):
        raise TimingSummaryError(f"fingerprint must be an object: {path}")
    return MachineFingerprint.from_dict(payload)


def summarize_candidate(path: Path, max_cv: float) -> TimingSummary:
    runs_root = path / "runs"
    run_directories = sorted(item for item in runs_root.iterdir() if item.is_dir()) \
        if runs_root.is_dir() else []
    if not run_directories:
        raise TimingSummaryError(f"no run directories found under {runs_root}")
    evidence = []
    for run_directory in run_directories:
        evidence.append(
            TimingRun(
                name=run_directory.name,
                fingerprint=_load_fingerprint(run_directory / "fingerprint.json"),
                rows=tuple(parse_reports(run_directory / "results")),
            )
        )
    return summarize_runs(evidence, max_cv=max_cv)


def _json_number(value: float) -> float | None:
    return value if math.isfinite(value) else None


def summary_payload(summary: TimingSummary) -> dict[str, object]:
    return {
        "schemaVersion": 2,
        "fingerprint": summary.fingerprint.to_dict() if summary.fingerprint else None,
        "fingerprintId": summary.fingerprint.identifier if summary.fingerprint else None,
        "runCount": summary.run_count,
        "eligible": summary.eligible,
        "reasons": list(summary.reasons),
        "rows": {
            key: {
                "runMeansNs": list(row.run_means_ns),
                "medianNs": _json_number(row.median_ns),
                "minimumNs": _json_number(row.minimum_ns),
                "maximumNs": _json_number(row.maximum_ns),
                "standardDeviationNs": _json_number(row.standard_deviation_ns),
                "cv": _json_number(row.cv),
                "allocationMinBytes": _json_number(row.allocation_min_bytes),
                "allocationMaxBytes": _json_number(row.allocation_max_bytes),
                "eligible": row.eligible,
                "reasons": list(row.reasons),
            }
            for key, row in summary.rows.items()
        },
    }


def write_summary(path: Path, summary: TimingSummary) -> None:
    (path / "summary.json").write_text(
        json.dumps(summary_payload(summary), indent=2) + "\n", encoding="utf-8"
    )
    lines = [
        "# Benchmark timing summary",
        "",
        f"- Runs: {summary.run_count}",
        f"- Eligible: {'yes' if summary.eligible else 'no'}",
        f"- Fingerprint: {summary.fingerprint.identifier if summary.fingerprint else 'none'}",
    ]
    if summary.reasons:
        lines.extend(("", "## Ineligibility", ""))
        lines.extend(f"- {reason}" for reason in summary.reasons)
    lines.extend(
        (
            "",
            "| Benchmark | Median | CV | Range | Alloc range | Eligible |",
            "| --- | ---: | ---: | ---: | ---: | --- |",
        )
    )
    for key, row in summary.rows.items():
        row_reason = "; ".join(row.reasons)
        eligibility = "yes" if row.eligible else f"no: {row_reason}"
        lines.append(
            f"| `{key}` | {row.median_ns:.2f} ns | {row.cv:.2%} | "
            f"{row.minimum_ns:.2f}–{row.maximum_ns:.2f} ns | "
            f"{row.allocation_min_bytes:.0f}–{row.allocation_max_bytes:.0f} B | {eligibility} |"
        )
    (path / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--max-cv", type=float, default=0.05)
    parser.add_argument("--max-regression", type=float, default=0.10)
    parser.add_argument("--enforce", action="store_true")
    parser.add_argument("--promote", action="store_true")
    args = parser.parse_args()

    try:
        candidate = summarize_candidate(args.candidate, args.max_cv)
        write_summary(args.candidate, candidate)
        comparison = None
        if args.baseline is not None and args.baseline.exists():
            baseline = summarize_candidate(args.baseline, args.max_cv)
            comparison = compare_summaries(
                baseline,
                candidate,
                max_regression=args.max_regression,
                enforce=args.enforce,
            )
            comparison_payload = {
                "enforced": args.enforce,
                "failed": comparison.failed,
                "rows": {
                    key: {
                        "baselineMedianNs": row.baseline_median_ns,
                        "candidateMedianNs": row.candidate_median_ns,
                        "change": _json_number(row.change) if row.change is not None else None,
                        "status": row.status,
                    }
                    for key, row in comparison.rows.items()
                },
            }
            (args.candidate / "comparison.json").write_text(
                json.dumps(comparison_payload, indent=2) + "\n", encoding="utf-8"
            )
        if args.promote:
            if args.baseline is None:
                raise TimingSummaryError("--promote requires --baseline as the accepted destination")
            promote_candidate(args.candidate, args.baseline, promote=True)
    except (BenchmarkResultError, TimingSummaryError) as error:
        print(f"timing summary error: {error}", file=sys.stderr)
        return 1

    print(f"candidate: {args.candidate}")
    print(f"eligible: {'yes' if candidate.eligible else 'no'}")
    for reason in candidate.reasons:
        print(f"ineligible: {reason}")
    return 1 if comparison is not None and comparison.failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
