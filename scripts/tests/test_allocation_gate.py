import json
import tempfile
import unittest
from pathlib import Path

from scripts.performance.allocation_gate import (
    AllocationBaseline,
    AllocationBudget,
    check_allocations,
    default_maximum_bytes,
    load_baseline,
    write_candidate,
)
from scripts.performance.benchmark_results import (
    BenchmarkResultError,
    BenchmarkRow,
    parse_reports,
)


def benchmark_row(key: str, allocated_bytes: float) -> BenchmarkRow:
    class_name, method = key.split(".", 1)
    return BenchmarkRow(
        key=key,
        class_name=class_name,
        method=method,
        parameters={},
        mean_ns=100,
        allocated_bytes=allocated_bytes,
        source=Path("result.csv"),
    )


def baseline(**budgets: tuple[float, float]) -> AllocationBaseline:
    return AllocationBaseline(
        schema_version=1,
        source_environment="test-machine",
        runtime=".NET 10",
        rows={
            key: AllocationBudget(accepted_bytes=accepted, maximum_bytes=maximum)
            for key, (accepted, maximum) in budgets.items()
        },
    )


class AllocationGateTests(unittest.TestCase):
    def test_exact_allocation_passes(self) -> None:
        report = check_allocations(
            [benchmark_row("Bench.Run", 1000)],
            baseline(**{"Bench.Run": (1000, 1064)}),
        )

        self.assertTrue(report.passed)
        self.assertEqual(report.findings[0].status, "pass")
        self.assertEqual(report.findings[0].delta_bytes, 0)

    def test_default_allowance_is_larger_of_64_bytes_or_five_percent(self) -> None:
        self.assertEqual(default_maximum_bytes(1000), 1064)
        self.assertEqual(default_maximum_bytes(10_000), 10_500)

    def test_allocation_above_maximum_fails(self) -> None:
        report = check_allocations(
            [benchmark_row("Bench.Run", 1065)],
            baseline(**{"Bench.Run": (1000, 1064)}),
        )

        self.assertFalse(report.passed)
        self.assertEqual(report.findings[0].status, "over-budget")

    def test_missing_baseline_row_fails(self) -> None:
        report = check_allocations([], baseline(**{"Bench.Run": (1000, 1064)}))

        self.assertFalse(report.passed)
        self.assertEqual(report.findings[0].status, "missing")

    def test_unexpected_result_row_fails(self) -> None:
        report = check_allocations([benchmark_row("Bench.New", 10)], baseline())

        self.assertFalse(report.passed)
        self.assertEqual(report.findings[0].status, "unexpected")

    def test_duplicate_result_rows_fail(self) -> None:
        row = benchmark_row("Bench.Run", 1000)

        with self.assertRaisesRegex(BenchmarkResultError, "duplicate.*Bench.Run"):
            check_allocations([row, row], baseline(**{"Bench.Run": (1000, 1064)}))

    def test_parser_duplicate_failure_is_propagated(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "Bench-report.csv"
            report.write_text(
                "Method,WarmupCount,Mean,Allocated\n"
                "Run,3,1 ns,1 B\n"
                "Run,3,1 ns,1 B\n",
                encoding="utf-8",
            )

            with self.assertRaises(BenchmarkResultError):
                check_allocations(parse_reports(report), baseline())


class CandidateTests(unittest.TestCase):
    def test_candidate_contains_review_ceiling_and_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            candidate = Path(temporary) / "candidate.json"

            write_candidate(
                [benchmark_row("Bench.Run", 1000)],
                candidate,
                source_environment="Apple Arm64",
                runtime=".NET 10.0.14",
            )
            loaded = load_baseline(candidate)

            self.assertEqual(loaded.source_environment, "Apple Arm64")
            self.assertEqual(loaded.rows["Bench.Run"].accepted_bytes, 1000)
            self.assertEqual(loaded.rows["Bench.Run"].maximum_bytes, 1064)

    def test_candidate_never_overwrites_input_baseline(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            accepted = Path(temporary) / "accepted.json"
            original = {"sentinel": True}
            accepted.write_text(json.dumps(original), encoding="utf-8")

            with self.assertRaisesRegex(BenchmarkResultError, "accepted baseline"):
                write_candidate(
                    [benchmark_row("Bench.Run", 1000)],
                    accepted,
                    source_environment="test",
                    runtime=".NET 10",
                    accepted_baseline=accepted,
                )

            self.assertEqual(json.loads(accepted.read_text(encoding="utf-8")), original)


if __name__ == "__main__":
    unittest.main()
