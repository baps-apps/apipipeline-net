"""Strict parsing for BenchmarkDotNet CSV reports."""

from __future__ import annotations

import csv
import re
from dataclasses import dataclass
from pathlib import Path
from types import MappingProxyType
from typing import Literal, Mapping, Sequence


class BenchmarkResultError(ValueError):
    """Raised when benchmark evidence is incomplete or ambiguous."""


@dataclass(frozen=True)
class BenchmarkRow:
    key: str
    class_name: str
    method: str
    parameters: Mapping[str, str]
    mean_ns: float
    allocated_bytes: float
    source: Path


_MEASUREMENT = re.compile(
    r"^\s*(?P<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*(?P<unit>\S+)\s*$"
)
_TIME_FACTORS = {
    "ns": 1.0,
    "us": 1_000.0,
    "µs": 1_000.0,
    "μs": 1_000.0,
    "ms": 1_000_000.0,
}
_BYTE_FACTORS = {"B": 1.0, "KB": 1024.0, "MB": 1024.0 * 1024.0}


def parse_measurement(value: str, quantity: Literal["time", "bytes"]) -> float:
    """Convert a BenchmarkDotNet measurement to nanoseconds or bytes."""

    normalized = value.strip()
    if not normalized or normalized.upper() == "NA":
        raise BenchmarkResultError(f"missing {quantity} measurement: {value!r}")

    match = _MEASUREMENT.match(normalized.replace(",", ""))
    if match is None:
        raise BenchmarkResultError(f"invalid {quantity} measurement: {value!r}")

    factors = _TIME_FACTORS if quantity == "time" else _BYTE_FACTORS
    unit = match.group("unit")
    if unit not in factors:
        raise BenchmarkResultError(f"unknown {quantity} unit {unit!r} in {value!r}")

    return float(match.group("value")) * factors[unit]


def row_key(class_name: str, method: str, parameters: Mapping[str, str]) -> str:
    """Build a stable identity independent of CSV parameter-column order."""

    if not parameters:
        return f"{class_name}.{method}"
    rendered = ",".join(f"{name}={parameters[name]}" for name in sorted(parameters))
    return f"{class_name}.{method}[{rendered}]"


def _class_name(report: Path) -> str:
    name = report.name
    suffix = "-report.csv"
    if not name.endswith(suffix):
        raise BenchmarkResultError(f"{report}: expected a *-report.csv filename")
    qualified_name = name[: -len(suffix)]
    return qualified_name.rsplit(".", 1)[-1]


def _report_files(path: Path) -> Sequence[Path]:
    if path.is_file():
        return [path]
    if not path.is_dir():
        raise BenchmarkResultError(f"{path}: benchmark report path does not exist")
    reports = sorted(path.rglob("*-report.csv"))
    if not reports:
        raise BenchmarkResultError(f"{path}: no *-report.csv files found")
    return reports


def _parse_report(report: Path) -> list[BenchmarkRow]:
    class_name = _class_name(report)
    try:
        handle = report.open(newline="", encoding="utf-8-sig")
    except OSError as error:
        raise BenchmarkResultError(f"{report}: cannot read report: {error}") from error

    with handle:
        reader = csv.DictReader(handle)
        headers = reader.fieldnames or []
        required = ("Method", "WarmupCount", "Mean", "Allocated")
        missing = [column for column in required if column not in headers]
        if missing:
            raise BenchmarkResultError(
                f"{report}: row 1: missing required column(s): {', '.join(missing)}"
            )

        warmup_index = headers.index("WarmupCount")
        mean_index = headers.index("Mean")
        if warmup_index >= mean_index:
            raise BenchmarkResultError(
                f"{report}: row 1: WarmupCount must precede Mean"
            )
        parameter_names = headers[warmup_index + 1 : mean_index]

        rows: list[BenchmarkRow] = []
        for row_number, raw in enumerate(reader, start=2):
            method = (raw.get("Method") or "").strip()
            if not method:
                raise BenchmarkResultError(f"{report}: row {row_number}: empty Method")
            parameters = {
                name: (raw.get(name) or "").strip() for name in parameter_names
            }
            try:
                mean_ns = parse_measurement(raw.get("Mean") or "", "time")
                allocated_bytes = parse_measurement(
                    raw.get("Allocated") or "", "bytes"
                )
            except BenchmarkResultError as error:
                raise BenchmarkResultError(
                    f"{report}: row {row_number}: {error}"
                ) from error

            immutable_parameters = MappingProxyType(dict(parameters))
            rows.append(
                BenchmarkRow(
                    key=row_key(class_name, method, immutable_parameters),
                    class_name=class_name,
                    method=method,
                    parameters=immutable_parameters,
                    mean_ns=mean_ns,
                    allocated_bytes=allocated_bytes,
                    source=report,
                )
            )
        return rows


def parse_reports(path: Path) -> list[BenchmarkRow]:
    """Parse one report or all reports below a result directory."""

    rows: list[BenchmarkRow] = []
    seen: dict[str, BenchmarkRow] = {}
    for report in _report_files(Path(path)):
        for row_number, row in enumerate(_parse_report(report), start=2):
            previous = seen.get(row.key)
            if previous is not None:
                raise BenchmarkResultError(
                    f"{report}: row {row_number}: duplicate benchmark key {row.key!r}; "
                    f"first seen in {previous.source}"
                )
            seen[row.key] = row
            rows.append(row)
    return rows
