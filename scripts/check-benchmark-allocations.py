#!/usr/bin/env python3
"""Check BenchmarkDotNet managed allocations against reviewed ceilings."""

from __future__ import annotations

import argparse
import platform
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from scripts.performance.allocation_gate import (  # noqa: E402
    check_allocations,
    load_baseline,
    write_candidate,
)
from scripts.performance.benchmark_results import (  # noqa: E402
    BenchmarkResultError,
    parse_reports,
)


def _format_number(value: object) -> str:
    if value is None:
        return "-"
    return f"{float(value):.0f}"


def _format_percent(value: object) -> str:
    if value is None:
        return "-"
    number = float(value)
    if number == float("inf"):
        return "+inf%"
    return f"{number:+.2f}%"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("results", type=Path)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("--write-candidate", type=Path)
    parser.add_argument(
        "--source-environment",
        default=f"{platform.system()} {platform.machine()} {platform.processor()}".strip(),
    )
    parser.add_argument("--runtime", default=".NET 10")
    args = parser.parse_args()

    try:
        rows = parse_reports(args.results)
        if args.write_candidate is not None:
            write_candidate(
                rows,
                args.write_candidate,
                source_environment=args.source_environment,
                runtime=args.runtime,
                accepted_baseline=args.baseline,
            )

        if not args.baseline.exists():
            if args.write_candidate is not None:
                print(f"Wrote allocation candidate: {args.write_candidate}")
                return 0
            raise BenchmarkResultError(f"accepted baseline does not exist: {args.baseline}")

        report = check_allocations(rows, load_baseline(args.baseline))
    except BenchmarkResultError as error:
        print(f"allocation gate error: {error}", file=sys.stderr)
        return 1

    print("key\taccepted\tcurrent\tdelta\tdelta %\tstatus")
    for finding in report.findings:
        print(
            "\t".join(
                (
                    finding.key,
                    _format_number(finding.accepted_bytes),
                    _format_number(finding.current_bytes),
                    _format_number(finding.delta_bytes),
                    _format_percent(finding.delta_percent),
                    finding.status,
                )
            )
        )
    return 0 if report.passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
