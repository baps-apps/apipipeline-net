"""Parse functional k6 evidence and render deterministic endpoint-load reports."""

from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Mapping, Sequence


class LoadSummaryError(ValueError):
    """Raised when a k6 summary is incomplete or structurally unsafe to report."""


EXPECTED_PROFILES = {
    "baseline": "baseline",
    "pipeline": "pipeline",
    "redis": "redis",
    "cache-hit": "pipeline",
    "cache-miss": "pipeline",
    "redis-reject": "redis",
}


@dataclass(frozen=True)
class LoadScenarioResult:
    scenario: str
    profile: str
    request_count: int
    requests_per_second: float
    median_ms: float
    p95_ms: float
    p99_ms: float
    failed_requests: int
    check_failures: int
    unexpected_statuses: int
    profile_mismatches: int
    status_counts: Mapping[int, int]
    functional_success: bool
    functional_reasons: tuple[str, ...]


@dataclass(frozen=True)
class LoadAggregate:
    results: tuple[LoadScenarioResult, ...]
    pipeline_p95_delta: float | None
    functional_success: bool


def _mapping(value: object, description: str) -> Mapping[str, object]:
    if not isinstance(value, dict):
        raise LoadSummaryError(f"{description} is missing or is not an object")
    return value


def _number(value: object, description: str) -> float:
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        raise LoadSummaryError(f"{description} is missing or is not numeric")
    converted = float(value)
    if not math.isfinite(converted) or converted < 0:
        raise LoadSummaryError(f"{description} must be finite and non-negative")
    return converted


def _metric_values(metrics: Mapping[str, object], name: str) -> Mapping[str, object]:
    metric = _mapping(metrics.get(name), f"metric {name!r}")
    return _mapping(metric.get("values"), f"metric {name!r} values")


def _metric_number(
    metrics: Mapping[str, object], metric_name: str, value_name: str
) -> float:
    values = _metric_values(metrics, metric_name)
    return _number(values.get(value_name), f"{metric_name}.{value_name}")


def parse_k6_summary(payload: Mapping[str, object]) -> LoadScenarioResult:
    """Extract measured-phase metrics. Functional faults are data; absent evidence is an error."""

    load = _mapping(payload.get("load"), "load metadata")
    scenario = load.get("scenario")
    profile = load.get("profile")
    if not isinstance(scenario, str) or scenario not in EXPECTED_PROFILES:
        raise LoadSummaryError(f"unknown or missing load scenario: {scenario!r}")
    if not isinstance(profile, str) or not profile:
        raise LoadSummaryError("load profile is missing")
    measure_duration_seconds = _number(
        load.get("measureDurationSeconds"), "load.measureDurationSeconds"
    )
    if measure_duration_seconds == 0:
        raise LoadSummaryError("load.measureDurationSeconds must be positive")

    metrics = _mapping(payload.get("metrics"), "metrics")
    request_count_value = _metric_number(
        metrics, "http_reqs{phase:measure}", "count"
    )
    if not request_count_value.is_integer() or request_count_value < 1:
        raise LoadSummaryError("measured request count must be a positive integer")
    request_count = int(request_count_value)
    # A filtered k6 Counter rate is divided by the complete test-process duration,
    # including a scenario's warmup start offset. The measured window is explicit.
    requests_per_second = request_count / measure_duration_seconds
    median_ms = _metric_number(
        metrics, "http_req_duration{phase:measure}", "med"
    )
    p95_ms = _metric_number(
        metrics, "http_req_duration{phase:measure}", "p(95)"
    )
    p99_ms = _metric_number(
        metrics, "http_req_duration{phase:measure}", "p(99)"
    )
    failure_rate = _metric_number(
        metrics, "http_req_failed{phase:measure}", "rate"
    )
    if failure_rate > 1:
        raise LoadSummaryError("http request failure rate cannot exceed 1")
    failed_requests = round(request_count * failure_rate)
    check_failures_value = _metric_number(
        metrics, "checks{phase:measure}", "fails"
    )
    if not check_failures_value.is_integer():
        raise LoadSummaryError("check failure count must be an integer")
    check_failures = int(check_failures_value)

    unexpected_value = _metric_number(metrics, "unexpected_statuses", "count")
    mismatches_value = _metric_number(metrics, "profile_mismatches", "count")
    expected_value = _metric_number(metrics, "expected_statuses", "count")
    for value, name in (
        (unexpected_value, "unexpected status count"),
        (mismatches_value, "profile mismatch count"),
        (expected_value, "expected status count"),
    ):
        if not value.is_integer():
            raise LoadSummaryError(f"{name} must be an integer")
    unexpected_statuses = int(unexpected_value)
    profile_mismatches = int(mismatches_value)

    status_counts: dict[int, int] = {}
    for name in ("status_200", "status_429"):
        count = _metric_number(metrics, name, "count")
        if not count.is_integer():
            raise LoadSummaryError(f"{name} count must be an integer")
        status_counts[int(name.removeprefix("status_"))] = int(count)

    reasons: list[str] = []
    if profile != EXPECTED_PROFILES[scenario]:
        reasons.append(
            f"scenario {scenario!r} declared profile {profile!r}, expected "
            f"{EXPECTED_PROFILES[scenario]!r}"
        )
    if failed_requests:
        reasons.append(f"{failed_requests} HTTP request(s) failed")
    if check_failures:
        reasons.append(f"{check_failures} check(s) failed")
    if unexpected_statuses:
        reasons.append(f"{unexpected_statuses} unexpected status response(s)")
    if profile_mismatches:
        reasons.append(f"{profile_mismatches} profile header mismatch(es)")
    if int(expected_value) != request_count:
        reasons.append(
            f"expected-status count {int(expected_value)} does not match request count "
            f"{request_count}"
        )

    return LoadScenarioResult(
        scenario=scenario,
        profile=profile,
        request_count=request_count,
        requests_per_second=requests_per_second,
        median_ms=median_ms,
        p95_ms=p95_ms,
        p99_ms=p99_ms,
        failed_requests=failed_requests,
        check_failures=check_failures,
        unexpected_statuses=unexpected_statuses,
        profile_mismatches=profile_mismatches,
        status_counts=status_counts,
        functional_success=not reasons,
        functional_reasons=tuple(reasons),
    )


def aggregate_results(results: Sequence[LoadScenarioResult]) -> LoadAggregate:
    if not results:
        raise LoadSummaryError("no load-test results were supplied")
    by_scenario: dict[str, LoadScenarioResult] = {}
    for result in results:
        if result.scenario in by_scenario:
            raise LoadSummaryError(f"duplicate load scenario {result.scenario!r}")
        by_scenario[result.scenario] = result

    delta = None
    if "baseline" in by_scenario and "pipeline" in by_scenario:
        baseline = by_scenario["baseline"].p95_ms
        pipeline = by_scenario["pipeline"].p95_ms
        if baseline == 0:
            delta = math.inf if pipeline > 0 else 0.0
        else:
            delta = pipeline / baseline - 1.0
    return LoadAggregate(
        results=tuple(sorted(results, key=lambda item: item.scenario)),
        pipeline_p95_delta=delta,
        functional_success=all(result.functional_success for result in results),
    )


def _json_result(result: LoadScenarioResult) -> dict[str, object]:
    payload = asdict(result)
    payload["status_counts"] = {
        str(status): count for status, count in result.status_counts.items()
    }
    return payload


def write_reports(
    aggregate: LoadAggregate,
    *,
    json_path: Path,
    markdown_path: Path,
    metadata: Mapping[str, object],
) -> None:
    json_payload = {
        "schemaVersion": 1,
        "functionalSuccess": aggregate.functional_success,
        "pipelineP95Delta": aggregate.pipeline_p95_delta
        if aggregate.pipeline_p95_delta is None
        or math.isfinite(aggregate.pipeline_p95_delta)
        else None,
        "metadata": metadata,
        "scenarios": [_json_result(result) for result in aggregate.results],
    }
    json_path.write_text(
        json.dumps(json_payload, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )

    lines = [
        "# Endpoint load-test summary",
        "",
        f"- Functional checks: {'pass' if aggregate.functional_success else 'fail'}",
    ]
    if aggregate.pipeline_p95_delta is not None:
        lines.append(
            "- Pipeline p95 delta from baseline: "
            + (
                f"{aggregate.pipeline_p95_delta:+.2%}"
                if math.isfinite(aggregate.pipeline_p95_delta)
                else "unbounded"
            )
            + " (informational; project target ≤ +20%)"
        )
    lines.extend(
        (
            "",
            "| Scenario | Profile | req/s | p50 | p95 | p99 | Failed | Checks | Statuses |",
            "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |",
        )
    )
    for result in aggregate.results:
        statuses = ", ".join(
            f"{status}={count}" for status, count in sorted(result.status_counts.items())
        )
        lines.append(
            f"| {result.scenario} | {result.profile} | {result.requests_per_second:.2f} | "
            f"{result.median_ms:.3f} ms | {result.p95_ms:.3f} ms | "
            f"{result.p99_ms:.3f} ms | {result.failed_requests} | "
            f"{result.check_failures} | {statuses} |"
        )
        lines.extend(f"  - {reason}" for reason in result.functional_reasons)
    markdown_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-dir", type=Path, required=True)
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--output-json", type=Path, required=True)
    parser.add_argument("--output-markdown", type=Path, required=True)
    args = parser.parse_args()

    try:
        raw_paths = sorted(args.input_dir.glob("*.json"))
        if not raw_paths:
            raise LoadSummaryError(f"no k6 JSON summaries found in {args.input_dir}")
        results = []
        for path in raw_paths:
            payload = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(payload, dict):
                raise LoadSummaryError(f"summary must be an object: {path}")
            results.append(parse_k6_summary(payload))
        metadata = json.loads(args.metadata.read_text(encoding="utf-8"))
        if not isinstance(metadata, dict):
            raise LoadSummaryError("environment metadata must be an object")
        aggregate = aggregate_results(results)
        write_reports(
            aggregate,
            json_path=args.output_json,
            markdown_path=args.output_markdown,
            metadata=metadata,
        )
    except (OSError, json.JSONDecodeError, LoadSummaryError) as error:
        print(f"load summary error: {error}", file=sys.stderr)
        return 1

    print(args.output_markdown.read_text(encoding="utf-8"), end="")
    return 0 if aggregate.functional_success else 1


if __name__ == "__main__":
    raise SystemExit(main())
