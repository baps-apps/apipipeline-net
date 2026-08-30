import tempfile
import unittest
from pathlib import Path

from scripts.performance.benchmark_results import (
    BenchmarkResultError,
    parse_measurement,
    parse_reports,
    row_key,
)


FIXTURE = Path(__file__).parent / "fixtures" / "benchmark-report.csv"


class MeasurementTests(unittest.TestCase):
    def test_converts_byte_units(self) -> None:
        self.assertEqual(parse_measurement("64 B", "bytes"), 64)
        self.assertEqual(parse_measurement("1.5 KB", "bytes"), 1536)
        self.assertEqual(parse_measurement("2 MB", "bytes"), 2 * 1024 * 1024)

    def test_converts_time_units_to_nanoseconds(self) -> None:
        self.assertEqual(parse_measurement("12 ns", "time"), 12)
        self.assertEqual(parse_measurement("1.25 µs", "time"), 1250)
        self.assertEqual(parse_measurement("1.25 μs", "time"), 1250)
        self.assertEqual(parse_measurement("2 ms", "time"), 2_000_000)

    def test_rejects_na_and_unknown_units(self) -> None:
        for value, quantity in (("NA", "bytes"), ("12 GB", "bytes"), ("4 s", "time")):
            with self.subTest(value=value, quantity=quantity):
                with self.assertRaises(BenchmarkResultError):
                    parse_measurement(value, quantity)


class RowKeyTests(unittest.TestCase):
    def test_parameter_order_does_not_change_key(self) -> None:
        expected = "Bench.Run[a=1,z=2]"
        self.assertEqual(row_key("Bench", "Run", {"z": "2", "a": "1"}), expected)
        self.assertEqual(row_key("Bench", "Run", {"a": "1", "z": "2"}), expected)


class ReportParsingTests(unittest.TestCase):
    def _write_report(self, directory: Path, contents: str, name: str = "bad-report.csv") -> Path:
        report = directory / name
        report.write_text(contents, encoding="utf-8")
        return report

    def test_parses_fixture_and_derives_class_from_filename(self) -> None:
        rows = parse_reports(FIXTURE)

        self.assertEqual(len(rows), 2)
        self.assertEqual(rows[0].class_name, "benchmark")
        self.assertEqual(rows[0].parameters, {"Mode": "miss", "PayloadSize": "1024"})
        self.assertEqual(rows[0].key, "benchmark.Execute[Mode=miss,PayloadSize=1024]")
        self.assertEqual(rows[0].mean_ns, 1250)
        self.assertEqual(rows[0].allocated_bytes, 1536)
        self.assertEqual(rows[1].allocated_bytes, 1024 * 1024)

    def test_rejects_missing_required_column_with_source(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            report = self._write_report(
                Path(temporary),
                "Method,WarmupCount,Mean\nRun,3,1 ns\n",
            )

            with self.assertRaisesRegex(BenchmarkResultError, rf"{report.name}.*Allocated"):
                parse_reports(report)

    def test_rejects_na_allocation_with_source_and_row(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            report = self._write_report(
                Path(temporary),
                "Method,WarmupCount,Mean,Allocated\nRun,3,1 ns,NA\n",
            )

            with self.assertRaisesRegex(BenchmarkResultError, rf"{report.name}.*row 2"):
                parse_reports(report)

    def test_rejects_unknown_unit_with_source_and_row(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            report = self._write_report(
                Path(temporary),
                "Method,WarmupCount,Mean,Allocated\nRun,3,1 fortnight,1 B\n",
            )

            with self.assertRaisesRegex(BenchmarkResultError, rf"{report.name}.*row 2"):
                parse_reports(report)

    def test_rejects_duplicate_keys_across_reports(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            duplicate = self._write_report(
                directory,
                "Method,WarmupCount,Mean,Allocated\n"
                "Run,3,1 ns,1 B\n"
                "Run,3,2 ns,2 B\n",
                "Bench-report.csv",
            )

            with self.assertRaisesRegex(BenchmarkResultError, rf"{duplicate.name}.*row 3.*duplicate"):
                parse_reports(directory)


if __name__ == "__main__":
    unittest.main()
