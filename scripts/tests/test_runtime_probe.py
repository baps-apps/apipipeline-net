from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


class RuntimeProbeTests(unittest.TestCase):
    def test_reports_executing_runtime_not_latest_installed_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            calls = root / "calls.txt"
            fake_dotnet = root / "dotnet"
            fake_dotnet.write_text(
                "#!/usr/bin/env bash\n"
                f"printf '%s\\n' \"$*\" >> {calls!s}\n"
                "if [[ \"${1:-}\" == --list-runtimes ]]; then\n"
                "  printf 'Microsoft.NETCore.App 10.0.14 [/fake]\\n'\n"
                "  printf 'Microsoft.NETCore.App 11.0.1 [/fake]\\n'\n"
                "  exit 0\n"
                "fi\n"
                "if [[ \"${1:-}\" == exec && \"${3:-}\" == --runtime-version ]]; then\n"
                "  printf '10.0.14\\n'\n"
                "  exit 0\n"
                "fi\n"
                "exit 2\n",
                encoding="utf-8",
            )
            fake_dotnet.chmod(0o755)
            app = root / "probe.dll"
            app.touch()

            result = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    "-m",
                    "scripts.performance.runtime_probe",
                    "--dotnet",
                    str(fake_dotnet),
                    "--app",
                    str(app),
                ],
                cwd=Path(__file__).resolve().parents[2],
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.strip(), "10.0.14")
            self.assertEqual(
                calls.read_text(encoding="utf-8").splitlines(),
                [f"exec {app} --runtime-version"],
            )


if __name__ == "__main__":
    unittest.main()
