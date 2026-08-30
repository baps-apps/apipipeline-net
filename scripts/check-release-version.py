#!/usr/bin/env python3
"""Fail unless the version being released is every package's <Version>.

Usage: check-release-version.py <requested>      (from the repository root)

<requested> is the release tag (v2.0.0) or the version typed into a manual run (2.0.0). A tag
pushed on a commit whose <Version> says something else would otherwise publish that other version
under the tag: v2.1.0 shipping packages versioned 2.0.0, and the tag pointing at a release that
does not exist. Each packable project's version is its own <Version>, else src/Shared/Shared.props.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

SEMVER = re.compile(r"^v?(\d+\.\d+\.\d+)$")


def prop(xml: str, name: str) -> str | None:
    match = re.search(rf"<{name}>\s*([^<]*?)\s*</{name}>", xml)
    return match[1] if match else None


def main(argv: list[str]) -> int:
    if len(argv) != 2 or not (match := SEMVER.match(argv[1].strip())):
        print(f"::error::Expected a release version such as v2.0.0 or 2.0.0, got {argv[1:]!r}.")
        return 1

    requested = match[1]
    shared_props = Path("src/Shared/Shared.props")
    shared_version = prop(shared_props.read_text(encoding="utf-8"), "Version")

    failed = False
    packages = 0
    for csproj in sorted(Path("src").glob("*/*.csproj")):
        xml = csproj.read_text(encoding="utf-8")
        if prop(xml, "IsPackable") != "true":
            continue

        packages += 1
        own = prop(xml, "Version")
        version = own or shared_version
        source = csproj if own else shared_props
        if version != requested:
            print(
                f"::error::{csproj.stem} is version {version} ({source}), but {requested} is being "
                f"released. Set <Version> to {requested}, or release {version}."
            )
            failed = True
        else:
            print(f"{csproj.stem} {version}: matches the release.")

    if packages == 0:
        print("::error::No packable projects found under src/.")
        return 1

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
