from __future__ import annotations

import json
import os
import signal
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

from scripts.performance.load_summary import (
    LoadSummaryError,
    aggregate_results,
    parse_k6_summary,
)


def metric(values: dict[str, float | int]) -> dict[str, object]:
    return {"type": "trend", "contains": "time", "values": values}


def summary(
    scenario: str = "baseline",
    profile: str = "baseline",
    *,
    median: float = 1.0,
    p95: float = 2.0,
    p99: float = 3.0,
    request_failure_rate: float = 0.0,
    check_failures: int = 0,
    unexpected_statuses: int = 0,
    profile_mismatches: int = 0,
) -> dict[str, object]:
    requests = 600
    return {
        "load": {
            "scenario": scenario,
            "profile": profile,
            "expectedStatus": 200,
            "measureDurationSeconds": 6.0,
        },
        "metrics": {
            "http_reqs{phase:measure}": {
                "type": "counter",
                # k6 divides the filtered counter by the entire process duration,
                # including the warmup start offset. The report must not use it.
                "values": {"count": requests, "rate": 75.0},
            },
            "http_req_duration{phase:measure}": metric(
                {"med": median, "p(95)": p95, "p(99)": p99}
            ),
            "http_req_failed{phase:measure}": {
                "type": "rate",
                "values": {
                    "rate": request_failure_rate,
                    "passes": int(requests * request_failure_rate),
                    "fails": int(requests * (1 - request_failure_rate)),
                },
            },
            "checks{phase:measure}": {
                "type": "rate",
                "values": {
                    "rate": (requests - check_failures) / requests,
                    "passes": requests - check_failures,
                    "fails": check_failures,
                },
            },
            "expected_statuses": {
                "type": "counter",
                "values": {"count": requests, "rate": 100.0},
            },
            "unexpected_statuses": {
                "type": "counter",
                "values": {"count": unexpected_statuses, "rate": 0.0},
            },
            "profile_mismatches": {
                "type": "counter",
                "values": {"count": profile_mismatches, "rate": 0.0},
            },
            "status_200": {
                "type": "counter",
                "values": {"count": requests - unexpected_statuses, "rate": 100.0},
            },
            "status_429": {
                "type": "counter",
                "values": {"count": 0, "rate": 0.0},
            },
        },
    }


class LoadSummaryTests(unittest.TestCase):
    def test_extracts_required_metrics(self) -> None:
        result = parse_k6_summary(summary())

        self.assertEqual(result.scenario, "baseline")
        self.assertEqual(result.profile, "baseline")
        self.assertEqual(result.requests_per_second, 100.0)
        self.assertEqual(result.median_ms, 1.0)
        self.assertEqual(result.p95_ms, 2.0)
        self.assertEqual(result.p99_ms, 3.0)
        self.assertEqual(result.status_counts, {200: 600, 429: 0})
        self.assertTrue(result.functional_success)

    def test_missing_percentile_fails_closed(self) -> None:
        payload = summary()
        del payload["metrics"]["http_req_duration{phase:measure}"]["values"]["p(99)"]

        with self.assertRaisesRegex(LoadSummaryError, r"p\(99\)"):
            parse_k6_summary(payload)

    def test_unexpected_status_or_check_failure_is_functional_failure(self) -> None:
        result = parse_k6_summary(
            summary(check_failures=1, unexpected_statuses=1)
        )

        self.assertFalse(result.functional_success)
        self.assertEqual(result.check_failures, 1)
        self.assertEqual(result.unexpected_statuses, 1)

    def test_profile_mismatch_is_functional_failure(self) -> None:
        result = parse_k6_summary(summary(profile_mismatches=2))

        self.assertFalse(result.functional_success)
        self.assertEqual(result.profile_mismatches, 2)

    def test_pipeline_p95_delta_is_exact_and_informational(self) -> None:
        aggregate = aggregate_results(
            [
                parse_k6_summary(summary("baseline", "baseline", p95=10.0)),
                parse_k6_summary(summary("pipeline", "pipeline", p95=12.0)),
            ]
        )

        self.assertAlmostEqual(aggregate.pipeline_p95_delta, 0.20)
        self.assertTrue(aggregate.functional_success)


class LoadRunnerCleanupTests(unittest.TestCase):
    def test_sigterm_cleans_fake_host_and_container(self) -> None:
        repository = Path(__file__).resolve().parents[2]
        runner = repository / "scripts" / "run-load-tests.sh"
        with tempfile.TemporaryDirectory() as temporary:
            probe = Path(temporary)
            docker_log = probe / "docker.log"
            fake_docker = probe / "docker"
            fake_docker.write_text(
                "#!/usr/bin/env bash\n"
                f"printf '%s\\n' \"$*\" >> {docker_log!s}\n",
                encoding="utf-8",
            )
            fake_docker.chmod(0o755)
            process = subprocess.Popen(
                [str(runner), "--cleanup-probe", str(probe)],
                cwd=repository,
                env={**os.environ, "DOCKER_CMD": str(fake_docker)},
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            try:
                deadline = time.monotonic() + 5
                while not (probe / "ready").exists() and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertTrue((probe / "ready").exists(), "cleanup probe did not start")
                process.send_signal(signal.SIGTERM)
                process.wait(timeout=5)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)

            self.assertTrue((probe / "host-terminated").exists())
            self.assertIn("stop probe-container", docker_log.read_text(encoding="utf-8"))

    def test_sigterm_during_k6_stops_host_container_and_client(self) -> None:
        repository = Path(__file__).resolve().parents[2]
        runner = repository / "scripts" / "run-load-tests.sh"
        with tempfile.TemporaryDirectory() as temporary:
            probe = Path(temporary)
            bin_directory = probe / "bin"
            bin_directory.mkdir()
            docker_log = probe / "docker.log"
            run_pid = probe / "docker-run.pid"
            ready = probe / "ready"
            host_terminated = probe / "host-terminated"

            fake_docker = bin_directory / "docker"
            fake_docker.write_text(
                "#!/usr/bin/env bash\n"
                f"printf '%s\\n' \"$*\" >> {docker_log!s}\n"
                "case \"${1:-}\" in\n"
                "  info) exit 0 ;;\n"
                "  run)\n"
                f"    printf '%s' \"$$\" > {run_pid!s}\n"
                f"    touch {ready!s}\n"
                "    trap 'exit 0' TERM INT\n"
                "    while true; do sleep 0.1; done\n"
                "    ;;\n"
                "  stop)\n"
                f"    if [[ -s {run_pid!s} ]]; then kill -TERM \"$(cat {run_pid!s})\" 2>/dev/null || true; fi\n"
                "    exit 0\n"
                "    ;;\n"
                "  ps) exit 0 ;;\n"
                "  version) printf '29.9.3\\n'; exit 0 ;;\n"
                "esac\n",
                encoding="utf-8",
            )
            fake_docker.chmod(0o755)

            fake_dotnet = bin_directory / "dotnet"
            fake_dotnet.write_text(
                "#!/usr/bin/env bash\n"
                "if [[ \"${1:-}\" == build ]]; then exit 0; fi\n"
                "if [[ \"${1:-}\" == --version ]]; then printf '10.0.403\\n'; exit 0; fi\n"
                "if [[ \"${1:-}\" == --list-runtimes ]]; then printf 'Microsoft.NETCore.App 10.0.14 [/fake]\\n'; exit 0; fi\n"
                "trap 'touch \"$APIPIPELINE_TEST_HOST_TERMINATED\"; exit 0' TERM INT\n"
                "while true; do sleep 0.1; done\n",
                encoding="utf-8",
            )
            fake_dotnet.chmod(0o755)

            fake_curl = bin_directory / "curl"
            fake_curl.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
            fake_curl.chmod(0o755)

            fake_python = bin_directory / "python3"
            fake_python.write_text(
                "#!/usr/bin/env bash\n"
                "if [[ $# -ge 3 ]]; then printf '%s\\n' \"${!#}\"; else printf '12345\\n'; fi\n",
                encoding="utf-8",
            )
            fake_python.chmod(0o755)

            process = subprocess.Popen(
                [str(runner), "--output", str(probe / "output")],
                cwd=repository,
                env={
                    **os.environ,
                    "PATH": f"{bin_directory}{os.pathsep}{os.environ['PATH']}",
                    "DOCKER_CMD": str(fake_docker),
                    "DOTNET_CMD": str(fake_dotnet),
                    "APIPIPELINE_TEST_HOST_TERMINATED": str(host_terminated),
                },
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                text=True,
            )
            exited = False
            try:
                deadline = time.monotonic() + 5
                while not ready.exists() and time.monotonic() < deadline:
                    time.sleep(0.05)
                failure = ""
                if process.poll() is not None and process.stderr is not None:
                    failure = process.stderr.read()
                log = docker_log.read_text(encoding="utf-8") if docker_log.exists() else ""
                self.assertTrue(
                    ready.exists(),
                    f"runner did not enter the k6 execution branch; exit={process.poll()}; "
                    f"stderr={failure!r}; docker={log!r}",
                )
                process.send_signal(signal.SIGTERM)
                try:
                    process.wait(timeout=2)
                    exited = True
                except subprocess.TimeoutExpired:
                    pass
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
                if run_pid.exists():
                    try:
                        os.kill(int(run_pid.read_text(encoding="utf-8")), signal.SIGTERM)
                    except (ProcessLookupError, ValueError):
                        pass
                if process.stderr is not None:
                    process.stderr.close()

            self.assertTrue(exited, "SIGTERM was deferred while the foreground Docker client ran")
            self.assertTrue(host_terminated.exists())
            self.assertIn("stop apipipeline-k6-baseline-", docker_log.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
