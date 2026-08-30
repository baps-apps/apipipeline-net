#!/usr/bin/env python3
"""Fail when a shipped package ID could be restored from a feed other than the private one.

Usage: check-source-mapping.py [repository-root]

The package IDs (ApiPipeline.NET, ApiPipeline.NET.*) carry no company prefix and are not reserved on
nuget.org, so anyone can publish a package there under one of them. NuGet package source mapping is
what keeps a restore on the private feed (dependency confusion). This check resolves every shipped
package ID the way NuGet does against:

- every committed nuget.config, and
- every NuGet configuration shown in the Markdown (the copy-paste setup consumers follow),

and fails unless exactly one source can serve it: the private feed. It also requires each
configuration to clear inherited sources, so a machine-wide source cannot join the restore. A new
package whose ID the patterns do not cover (say "ApiPipelineNET.Extras") fails here instead of
restoring from nuget.org in a consumer's build.

NuGet's rule: patterns are case-insensitive; an exact ID beats any prefix ("Foo.*"), a longer prefix
beats a shorter one, "*" matches everything; every source holding the winning pattern is asked.
"""
from __future__ import annotations

import os
import re
import sys
import xml.etree.ElementTree as ElementTree

PRIVATE_SOURCE_KEY = "baps-apps-packages"
PRIVATE_SOURCE_URL = "https://nuget.pkg.github.com/baps-apps/index.json"

SKIP = ("node_modules", f"docs{os.sep}archive", f"docs{os.sep}superpowers", ".superpowers", ".cursor",
        f"{os.sep}bin{os.sep}", f"{os.sep}obj{os.sep}", f"{os.sep}.git{os.sep}", ".codegraph",
        "BenchmarkDotNet.Artifacts")
XML_BLOCK = re.compile(r"^```xml[^\n]*\n(.*?)^```", re.MULTILINE | re.DOTALL)


class Config:
    """The parts of one NuGet configuration that decide where a package comes from."""

    def __init__(self, name: str, xml: str):
        self.name = name
        root = ElementTree.fromstring(xml)
        sources = root.find("packageSources")
        self.has_sources = sources is not None
        self.clears_sources = sources is not None and sources.find("clear") is not None
        self.source_urls = {
            add.get("key", ""): add.get("value", "")
            for add in (sources.findall("add") if sources is not None else [])
        }
        # source key -> patterns
        self.mapping: dict[str, list[str]] = {}
        mapping = root.find("packageSourceMapping")
        for source in (mapping.findall("packageSource") if mapping is not None else []):
            patterns = self.mapping.setdefault(source.get("key", ""), [])
            patterns.extend(package.get("pattern", "") for package in source.findall("package"))


def specificity(pattern: str, package_id: str) -> tuple[int, int] | None:
    """How specifically pattern matches package_id (higher wins), or None when it does not."""
    pattern = pattern.strip().lower()
    package_id = package_id.lower()
    if pattern.endswith("*"):
        prefix = pattern[:-1]
        return (0, len(prefix)) if package_id.startswith(prefix) else None
    return (1, len(pattern)) if pattern == package_id else None


def resolve(mapping: dict[str, list[str]], package_id: str) -> set[str]:
    """The sources NuGet asks for package_id: every source holding the most specific matching pattern."""
    best: tuple[int, int] | None = None
    sources: set[str] = set()
    for source, patterns in mapping.items():
        for pattern in patterns:
            score = specificity(pattern, package_id)
            if score is None:
                continue
            if best is None or score > best:
                best, sources = score, {source}
            elif score == best:
                sources.add(source)
    return sources


def problems(config: Config, package_ids: list[str]) -> list[str]:
    found: list[str] = []
    if not config.mapping:
        return [f"{config.name}: no <packageSourceMapping>, so every source is asked for every package"]
    if config.has_sources and not config.clears_sources:
        found.append(f"{config.name}: <packageSources> has no <clear />, so machine-wide sources join the restore")
    if config.has_sources and config.source_urls.get(PRIVATE_SOURCE_KEY) != PRIVATE_SOURCE_URL:
        found.append(f"{config.name}: source '{PRIVATE_SOURCE_KEY}' is not {PRIVATE_SOURCE_URL}")
    for package_id in package_ids:
        sources = resolve(config.mapping, package_id)
        if sources != {PRIVATE_SOURCE_KEY}:
            served = ", ".join(sorted(sources)) or "no source"
            found.append(f"{config.name}: {package_id} resolves to {served}; it must resolve to {PRIVATE_SOURCE_KEY} only")
    return found


def shipped_package_ids(root: str) -> list[str]:
    """PackageId of every packable project under src/."""
    ids: list[str] = []
    src = os.path.join(root, "src")
    for directory, _, names in os.walk(src):
        for name in names:
            if not name.endswith(".csproj") or any(part in os.path.join(directory, name) for part in SKIP):
                continue
            project = ElementTree.parse(os.path.join(directory, name)).getroot()
            if (project.findtext(".//IsPackable") or "").strip().lower() != "true":
                continue
            ids.append((project.findtext(".//PackageId") or name[: -len(".csproj")]).strip())
    return sorted(ids)


def configurations(root: str) -> list[Config]:
    """Every committed nuget.config, and every XML block in the Markdown that maps package sources."""
    found: list[Config] = []
    for directory, _, names in os.walk(root):
        for name in names:
            path = os.path.join(directory, name)
            if any(part in path for part in SKIP):
                continue
            relative = os.path.relpath(path, root)
            if name.lower() == "nuget.config":
                with open(path, encoding="utf-8-sig") as handle:
                    found.append(Config(relative, handle.read()))
            elif name.endswith(".md"):
                with open(path, encoding="utf-8") as handle:
                    text = handle.read()
                for match in XML_BLOCK.finditer(text):
                    block = match.group(1)
                    if "<packageSourceMapping" in block:
                        line = text.count("\n", 0, match.start()) + 1
                        found.append(Config(f"{relative}:{line}", block))
    return sorted(found, key=lambda config: config.name)


def main(argv: list[str]) -> int:
    root = os.path.abspath(argv[1] if len(argv) > 1 else ".")
    package_ids = shipped_package_ids(root)
    if not package_ids:
        print("No packable project found under src/.")
        return 1

    configs = configurations(root)
    found = [problem for config in configs for problem in problems(config, package_ids)]
    for problem in found:
        print(problem)

    if found:
        return 1
    print(f"{len(package_ids)} package ID(s) resolve only to {PRIVATE_SOURCE_KEY} in {len(configs)} configuration(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
