"""Report the runtime selected to execute a built .NET application."""

from __future__ import annotations

import argparse
import re
import subprocess
from pathlib import Path


_RUNTIME_VERSION = re.compile(
    r"^\d+\.\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?$"
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--app", type=Path, required=True)
    args = parser.parse_args()

    if not args.app.is_file():
        parser.error(f"application does not exist: {args.app}")

    result = subprocess.run(
        [args.dotnet, "exec", str(args.app), "--runtime-version"],
        capture_output=True,
        text=True,
        check=False,
    )
    version = result.stdout.strip()
    if result.returncode != 0:
        detail = result.stderr.strip() or f"exit code {result.returncode}"
        parser.error(f"runtime probe failed: {detail}")
    if not _RUNTIME_VERSION.fullmatch(version):
        parser.error(f"runtime probe returned an invalid version: {version!r}")

    print(version)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
