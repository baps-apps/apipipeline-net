from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from scripts.performance.benchmark_results import BenchmarkRow
from scripts.performance.timing_summary import (
    MachineFingerprint,
    TimingRun,
    TimingSummaryError,
    compare_summaries,
    promote_candidate,
    summarize_runs,
)


def fingerprint(
    cpu: str = "test cpu", revision: str = "abc123"
) -> MachineFingerprint:
    return MachineFingerprint(
        os="test os",
        architecture="arm64",
        cpu_model=cpu,
        dotnet_sdk="10.0.403",
        dotnet_runtime="10.0.14",
        docker_server="29.9.3",
        git_revision=revision,
        benchmark_job="Default",
    )


def row(mean_ns: float, key: str = "Bench.Run", allocated: float = 1000) -> BenchmarkRow:
    return BenchmarkRow(
        key=key,
        class_name="Bench",
        method="Run",
        parameters={},
        mean_ns=mean_ns,
        allocated_bytes=allocated,
        source=Path("report.csv"),
    )


def runs(means: list[float], *, machine: MachineFingerprint | None = None) -> list[TimingRun]:
    selected = machine or fingerprint()
    return [TimingRun(f"run-{index}", selected, (row(mean),)) for index, mean in enumerate(means, 1)]


class TimingStabilityTests(unittest.TestCase):
    def test_five_stable_same_fingerprint_runs_are_eligible(self) -> None:
        summary = summarize_runs(runs([100, 101, 99, 100, 100]))

        self.assertTrue(summary.eligible)
        result = summary.rows["Bench.Run"]
        self.assertTrue(result.eligible)
        self.assertEqual(result.median_ns, 100)
        self.assertLessEqual(result.cv, 0.05)
        self.assertEqual(result.allocation_min_bytes, 1000)
        self.assertEqual(result.allocation_max_bytes, 1000)

    def test_four_runs_are_ineligible(self) -> None:
        summary = summarize_runs(runs([100, 100, 100, 100]))

        self.assertFalse(summary.eligible)
        self.assertIn("exactly 5", " ".join(summary.reasons))

    def test_fingerprint_mismatch_is_ineligible(self) -> None:
        evidence = runs([100, 100, 100, 100, 100])
        evidence[-1] = TimingRun("run-5", fingerprint("different cpu"), (row(100),))

        summary = summarize_runs(evidence)

        self.assertFalse(summary.eligible)
        self.assertIn("fingerprint", " ".join(summary.reasons))

    def test_missing_row_is_ineligible(self) -> None:
        evidence = runs([100, 100, 100, 100, 100])
        evidence[-1] = TimingRun("run-5", fingerprint(), ())

        summary = summarize_runs(evidence)

        self.assertFalse(summary.eligible)
        self.assertFalse(summary.rows["Bench.Run"].eligible)
        self.assertIn("missing", " ".join(summary.rows["Bench.Run"].reasons))

    def test_cv_above_five_percent_is_ineligible(self) -> None:
        summary = summarize_runs(runs([80, 90, 100, 110, 120]))

        self.assertFalse(summary.eligible)
        self.assertGreater(summary.rows["Bench.Run"].cv, 0.05)

    def test_cv_uses_arithmetic_mean_as_denominator(self) -> None:
        summary = summarize_runs(runs([90, 90, 90, 90, 140]), max_cv=1.0)

        self.assertAlmostEqual(summary.rows["Bench.Run"].cv, 0.20)

    def test_non_default_benchmark_job_is_ineligible(self) -> None:
        dry_fingerprint = MachineFingerprint.from_dict(
            {**fingerprint().to_dict(), "benchmarkJob": "Dry"}
        )

        summary = summarize_runs(
            runs([100, 100, 100, 100, 100], machine=dry_fingerprint)
        )

        self.assertFalse(summary.eligible)
        self.assertIn("Default job", " ".join(summary.reasons))

    def test_missing_benchmark_job_fails_closed(self) -> None:
        payload = fingerprint().to_dict()
        payload.pop("benchmarkJob", None)

        with self.assertRaisesRegex(TimingSummaryError, "benchmarkJob"):
            MachineFingerprint.from_dict(payload)


class TimingComparisonTests(unittest.TestCase):
    def test_regression_is_informational_until_enforcement_is_requested(self) -> None:
        baseline = summarize_runs(runs([100, 100, 100, 100, 100]))
        candidate = summarize_runs(runs([115, 115, 115, 115, 115]))

        informational = compare_summaries(baseline, candidate, enforce=False)
        enforced = compare_summaries(baseline, candidate, enforce=True)

        self.assertFalse(informational.failed)
        self.assertEqual(informational.rows["Bench.Run"].status, "regression-informational")
        self.assertTrue(enforced.failed)
        self.assertEqual(enforced.rows["Bench.Run"].status, "regression")

    def test_same_machine_different_revisions_can_be_compared(self) -> None:
        baseline = summarize_runs(
            runs([100, 100, 100, 100, 100], machine=fingerprint(revision="abc123"))
        )
        candidate = summarize_runs(
            runs([115, 115, 115, 115, 115], machine=fingerprint(revision="def456"))
        )

        comparison = compare_summaries(baseline, candidate, enforce=False)

        self.assertEqual(
            comparison.rows["Bench.Run"].status, "regression-informational"
        )

    def test_enforcement_fails_for_non_default_job(self) -> None:
        dry = MachineFingerprint.from_dict(
            {**fingerprint().to_dict(), "benchmarkJob": "Dry"}
        )
        baseline = summarize_runs(runs([100, 100, 100, 100, 100], machine=dry))
        candidate = summarize_runs(runs([100, 100, 100, 100, 100], machine=dry))

        comparison = compare_summaries(baseline, candidate, enforce=True)

        self.assertTrue(comparison.failed)
        self.assertEqual(comparison.rows["Bench.Run"].status, "ineligible")


class PromotionTests(unittest.TestCase):
    def _write_summary(self, directory: Path, machine: MachineFingerprint) -> None:
        directory.mkdir(parents=True)
        (directory / "summary.json").write_text(
            json.dumps({"fingerprint": machine.to_dict(), "eligible": True, "rows": {}}),
            encoding="utf-8",
        )

    def test_promotion_requires_explicit_flag(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            candidate = root / "candidate"
            accepted = root / "accepted"
            self._write_summary(candidate, fingerprint())

            with self.assertRaisesRegex(TimingSummaryError, "--promote"):
                promote_candidate(candidate, accepted, promote=False)

            self.assertFalse(accepted.exists())

    def test_promotion_refuses_fingerprint_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            candidate = root / "candidate"
            accepted = root / "accepted"
            self._write_summary(candidate, fingerprint("candidate cpu"))
            self._write_summary(accepted, fingerprint("accepted cpu"))
            before = (accepted / "summary.json").read_text(encoding="utf-8")

            with self.assertRaisesRegex(TimingSummaryError, "fingerprint"):
                promote_candidate(candidate, accepted, promote=True)

            self.assertEqual((accepted / "summary.json").read_text(encoding="utf-8"), before)

    def test_promotion_refuses_non_default_job_even_if_summary_claims_eligible(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            candidate = root / "candidate"
            accepted = root / "accepted"
            dry = MachineFingerprint.from_dict(
                {**fingerprint().to_dict(), "benchmarkJob": "Dry"}
            )
            self._write_summary(candidate, dry)

            with self.assertRaisesRegex(TimingSummaryError, "Default job"):
                promote_candidate(candidate, accepted, promote=True)

            self.assertFalse(accepted.exists())


if __name__ == "__main__":
    unittest.main()
