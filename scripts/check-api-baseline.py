#!/usr/bin/env python3
"""Fail the build when the API-compatibility baseline is not the previous release.

Usage: check-api-baseline.py            (from the repository root, with tags fetched)

Package validation compares each package's public API against ApiCompatBaselineVersion
(Directory.Build.targets, or a project's own). That catches a break only against that one
version: left at 1.1.0 for all of 2.x, an API added in 2.0 and removed in 2.1 would pass. For
every packable project under src/, this checks:

1. The project's <Version> is not already released (tag v<Version>) unless HEAD is that tag.
   Otherwise the next change ships under a released version: the release workflow's push skips
   duplicates, so it would silently publish nothing.
2. The baseline is the newest release tag below <Version>, so each release is compared against
   the one before it. Move the baseline forward right after tagging a release.
3. No project hard-codes SkipApiCompatBaseline. A package with no release yet sets
   ApiCompatFirstVersion instead, so the comparison starts by itself once the baseline reaches
   that version.

Release tags are vMAJOR.MINOR.PATCH; pre-release tags are not baselines.
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

Version = tuple[int, int, int]

TAG = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
VERSION = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")


def parse(text: str) -> Version | None:
    match = VERSION.match(text.strip())
    return (int(match[1]), int(match[2]), int(match[3])) if match else None


def show(version: Version) -> str:
    return ".".join(map(str, version))


def prop(xml: str, name: str) -> str | None:
    match = re.search(rf"<{name}(?:\s[^>]*)?>([^<]*)</{name}>", xml)
    return match[1].strip() if match else None


def git(*args: str) -> list[str]:
    output = subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout
    return [line for line in output.splitlines() if line]


def main() -> int:
    releases = sorted(
        (int(m[1]), int(m[2]), int(m[3])) for tag in git("tag", "--list", "v*") if (m := TAG.match(tag))
    )
    if not releases:
        print("::error::No release tags (vX.Y.Z) found. Fetch tags (actions/checkout fetch-depth: 0).")
        return 1

    head_tags = {(int(m[1]), int(m[2]), int(m[3])) for tag in git("tag", "--points-at", "HEAD") if (m := TAG.match(tag))}
    shared = prop(Path("Directory.Build.targets").read_text(encoding="utf-8"), "ApiCompatBaselineVersion")
    # Every package's <Version>, unless a project sets its own.
    shared_props = Path("src/Shared/Shared.props")
    shared_version = prop(shared_props.read_text(encoding="utf-8"), "Version")

    failed = False
    for csproj in sorted(Path("src").glob("*/*.csproj")):
        xml = csproj.read_text(encoding="utf-8")
        if prop(xml, "IsPackable") != "true":
            continue

        name = csproj.stem
        version_file = csproj if prop(xml, "Version") is not None else shared_props
        version = parse(prop(xml, "Version") or shared_version or "")
        baseline_text = prop(xml, "ApiCompatBaselineVersion") or shared or ""
        baseline = parse(baseline_text)
        if version is None or baseline is None:
            print(f"::error::{name}: <Version> and ApiCompatBaselineVersion must be MAJOR.MINOR.PATCH.")
            failed = True
            continue

        errors = []
        if prop(xml, "SkipApiCompatBaseline") is not None:
            errors.append(
                f"{name} sets SkipApiCompatBaseline, which skips the comparison for good. A package with "
                "no release yet sets <ApiCompatFirstVersion> (its first release) instead."
            )

        if version in releases and version not in head_tags:
            errors.append(
                f"{name} {show(version)} is already released (tag v{show(version)}). Bump <Version> in "
                f"{version_file} before changing the package."
            )

        previous = [r for r in releases if r < version]
        expected = previous[-1] if previous else None
        if expected is not None and baseline != expected:
            errors.append(
                f"{name} {show(version)} is compared against {show(baseline)}, but the previous release is "
                f"{show(expected)}. Set ApiCompatBaselineVersion to {show(expected)} in "
                "Directory.Build.targets, then regenerate each CompatibilitySuppressions.xml "
                "(dotnet pack <project> -c Release -p:GenerateCompatibilitySuppressionFile=true) and keep "
                f"only breaks made since {show(expected)}."
            )

        for error in errors:
            print(f"::error::{error}")
        failed = failed or bool(errors)
        if errors:
            continue

        first = parse(prop(xml, "ApiCompatFirstVersion") or "")
        if first is not None and baseline < first:
            print(f"{name} {show(version)}: first released in {show(first)}, so not compared before then.")
        else:
            print(f"{name} {show(version)}: compared against {show(baseline)}, the previous release.")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
