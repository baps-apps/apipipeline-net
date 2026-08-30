#!/usr/bin/env python3
"""Fail the build when line coverage of a shipped assembly drops below its threshold.

Usage: check-coverage.py <cobertura.xml> <Assembly>=<min-percent> [<Assembly>=<min-percent> ...]

Reads the Cobertura report written by `dotnet test --coverage --coverage-output-format cobertura`
(Microsoft.Testing.Extensions.CodeCoverage). Only the named assemblies are gated; the test, perf and
sample assemblies are ignored. An assembly missing from the report fails the check, so a renamed
assembly cannot silently drop out of the gate.
"""
import sys
import xml.etree.ElementTree as ET


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print(__doc__)
        return 2

    root = ET.parse(argv[1]).getroot()
    rates = {}
    for package in root.iter("package"):
        lines = [line for cls in package.iter("class") for line in cls.iter("line")]
        if lines:
            covered = sum(1 for line in lines if int(line.get("hits", "0")) > 0)
            rates[package.get("name")] = (covered, len(lines))

    failed = False
    for spec in argv[2:]:
        name, _, minimum = spec.partition("=")
        if name not in rates:
            print(f"::error::{name}: not found in the coverage report")
            failed = True
            continue

        covered, total = rates[name]
        percent = 100.0 * covered / total
        status = "ok" if percent >= float(minimum) else "BELOW THRESHOLD"
        print(f"{name}: {percent:.1f}% of {total} lines (minimum {minimum}%) {status}")
        if percent < float(minimum):
            print(f"::error::{name} line coverage {percent:.1f}% is below the {minimum}% minimum")
            failed = True

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
