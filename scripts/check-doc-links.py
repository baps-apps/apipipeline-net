#!/usr/bin/env python3
"""Fail when a relative link in the published Markdown points at a missing file or heading.

Usage: check-doc-links.py [repository-root]

Checks every `[text](target)` in the published Markdown (the archive and working notes are
skipped). A target is a path relative to the linking file, optionally with a `#fragment`; the
fragment must match a heading of the target file, slugged the way GitHub does. External links
(`https:`, `mailto:`, ...) are not fetched.
"""
import os
import re
import sys

SKIP = ("node_modules", f"docs{os.sep}archive", f"docs{os.sep}superpowers", ".superpowers", ".cursor",
        f"{os.sep}bin{os.sep}", f"{os.sep}obj{os.sep}", "BenchmarkDotNet.Artifacts")
FENCE = re.compile(r"^(```|~~~)")
HEADING = re.compile(r"^(#+)\s+(.*)")
LINK = re.compile(r"\]\(([^)\s]+)\)")


def slug(heading: str) -> str:
    text = heading.strip().lower().replace("`", "")
    text = re.sub(r"[^\w\- ]", "", text)
    return text.replace(" ", "-")


def anchors(path: str, cache: dict[str, set[str]]) -> set[str]:
    if path not in cache:
        found: set[str] = set()
        seen: dict[str, int] = {}
        in_code = False
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                if FENCE.match(line):
                    in_code = not in_code
                    continue
                match = None if in_code else HEADING.match(line)
                if match:
                    base = slug(match.group(2))
                    count = seen.get(base, 0)
                    seen[base] = count + 1
                    found.add(base if count == 0 else f"{base}-{count}")
        cache[path] = found
    return cache[path]


def links(path: str):
    in_code = False
    with open(path, encoding="utf-8") as handle:
        for number, line in enumerate(handle, 1):
            if FENCE.match(line):
                in_code = not in_code
                continue
            if not in_code:
                for match in LINK.finditer(line):
                    yield number, match.group(1)


def main(argv: list[str]) -> int:
    root = os.path.abspath(argv[1] if len(argv) > 1 else ".")
    cache: dict[str, set[str]] = {}
    broken = 0
    for directory, _, names in os.walk(root):
        for name in names:
            path = os.path.join(directory, name)
            if not name.endswith(".md") or any(part in path for part in SKIP):
                continue
            for number, target in links(path):
                if re.match(r"^[a-z][a-z0-9+.-]*:", target):
                    continue
                file_part, _, fragment = target.partition("#")
                resolved = os.path.normpath(os.path.join(directory, file_part)) if file_part else path
                where = f"{os.path.relpath(path, root)}:{number}"
                if not os.path.exists(resolved):
                    print(f"{where}: no such file: {target}")
                    broken += 1
                elif fragment and resolved.endswith(".md") and fragment not in anchors(resolved, cache):
                    print(f"{where}: no such heading: {target}")
                    broken += 1

    print(f"{broken} broken link(s)" if broken else "All relative links resolve.")
    return 1 if broken else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
