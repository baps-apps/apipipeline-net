"""Tests for scripts/check-source-mapping.py: NuGet's pattern precedence and the per-config checks.

Run: python3 -m unittest scripts.tests.test_source_mapping
"""
from __future__ import annotations

import importlib.util
import os
import tempfile
import unittest
from pathlib import Path

_SCRIPT = Path(__file__).resolve().parents[1] / "check-source-mapping.py"
_SPEC = importlib.util.spec_from_file_location("check_source_mapping", _SCRIPT)
assert _SPEC is not None and _SPEC.loader is not None
mapping = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(mapping)

PRIVATE = mapping.PRIVATE_SOURCE_KEY
IDS = ["ApiPipeline.NET", "ApiPipeline.NET.RateLimiting.Redis", "ApiPipeline.NET.Versioning"]


def config(mapping_xml: str, sources_xml: str | None = None) -> object:
    if sources_xml is None:
        sources_xml = (
            "<packageSources><clear />"
            '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
            f'<add key="{PRIVATE}" value="{mapping.PRIVATE_SOURCE_URL}" />'
            "</packageSources>"
        )
    return mapping.Config("test", f"<configuration>{sources_xml}<packageSourceMapping>{mapping_xml}</packageSourceMapping></configuration>")


SAFE = (
    f'<packageSource key="{PRIVATE}"><package pattern="ApiPipeline.NET" /><package pattern="ApiPipeline.NET.*" /></packageSource>'
    '<packageSource key="nuget.org"><package pattern="*" /></packageSource>'
)


class ResolveTests(unittest.TestCase):
    def test_exact_beats_star(self):
        self.assertEqual(mapping.resolve({PRIVATE: ["ApiPipeline.NET"], "nuget.org": ["*"]}, "ApiPipeline.NET"), {PRIVATE})

    def test_prefix_does_not_match_the_bare_id(self):
        # "ApiPipeline.NET.*" needs the dot: the core ID falls through to "*".
        self.assertEqual(mapping.resolve({PRIVATE: ["ApiPipeline.NET.*"], "nuget.org": ["*"]}, "ApiPipeline.NET"), {"nuget.org"})

    def test_longer_prefix_wins(self):
        sources = {PRIVATE: ["ApiPipeline.NET.*"], "nuget.org": ["ApiPipeline.*"]}
        self.assertEqual(mapping.resolve(sources, "ApiPipeline.NET.Versioning"), {PRIVATE})

    def test_exact_beats_longer_prefix(self):
        sources = {PRIVATE: ["ApiPipeline.NET.Versioning"], "nuget.org": ["ApiPipeline.NET.Versioning*"]}
        self.assertEqual(mapping.resolve(sources, "ApiPipeline.NET.Versioning"), {PRIVATE})

    def test_equal_patterns_ask_both_sources(self):
        sources = {PRIVATE: ["ApiPipeline.NET.*"], "nuget.org": ["ApiPipeline.NET.*"]}
        self.assertEqual(mapping.resolve(sources, "ApiPipeline.NET.Versioning"), {PRIVATE, "nuget.org"})

    def test_case_insensitive(self):
        self.assertEqual(mapping.resolve({PRIVATE: ["apipipeline.net.*"], "nuget.org": ["*"]}, "ApiPipeline.NET.Versioning"), {PRIVATE})

    def test_unmatched_id_has_no_source(self):
        self.assertEqual(mapping.resolve({PRIVATE: ["ApiPipeline.NET"]}, "Other"), set())


class ProblemTests(unittest.TestCase):
    def test_safe_config_passes(self):
        self.assertEqual(mapping.problems(config(SAFE), IDS), [])

    def test_missing_exact_pattern_fails_for_the_core_id_only(self):
        broken = SAFE.replace('<package pattern="ApiPipeline.NET" />', "")
        found = mapping.problems(config(broken), IDS)
        self.assertEqual(len(found), 1)
        self.assertIn("ApiPipeline.NET resolves to nuget.org", found[0])

    def test_tie_with_nuget_org_fails(self):
        tied = SAFE.replace('<package pattern="*" />', '<package pattern="*" /><package pattern="ApiPipeline.NET" />')
        self.assertTrue(any("baps-apps-packages, nuget.org" in p for p in mapping.problems(config(tied), IDS)))

    def test_sources_without_clear_fail(self):
        sources = f'<packageSources><add key="{PRIVATE}" value="{mapping.PRIVATE_SOURCE_URL}" /></packageSources>'
        self.assertTrue(any("no <clear />" in p for p in mapping.problems(config(SAFE, sources), IDS)))

    def test_private_key_pointing_elsewhere_fails(self):
        sources = f'<packageSources><clear /><add key="{PRIVATE}" value="https://api.nuget.org/v3/index.json" /></packageSources>'
        self.assertTrue(any("is not https://nuget.pkg.github.com" in p for p in mapping.problems(config(SAFE, sources), IDS)))

    def test_no_mapping_fails(self):
        found = mapping.problems(mapping.Config("test", "<configuration><packageSources><clear /></packageSources></configuration>"), IDS)
        self.assertEqual(len(found), 1)
        self.assertIn("no <packageSourceMapping>", found[0])

    def test_mapping_only_snippet_is_checked_without_sources(self):
        self.assertEqual(mapping.problems(config(SAFE, sources_xml=""), IDS), [])


class DiscoveryTests(unittest.TestCase):
    def test_finds_packable_ids_and_markdown_snippets(self):
        with tempfile.TemporaryDirectory() as root:
            project = Path(root, "src", "Pkg")
            project.mkdir(parents=True)
            (project / "Pkg.csproj").write_text(
                "<Project><PropertyGroup><IsPackable>true</IsPackable><PackageId>ApiPipeline.NET.Pkg</PackageId></PropertyGroup></Project>")
            tool = Path(root, "src", "Tool")
            tool.mkdir()
            (tool / "Tool.csproj").write_text("<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>")
            Path(root, "README.md").write_text(
                "text\n\n```xml\n<configuration><packageSourceMapping>" + SAFE + "</packageSourceMapping></configuration>\n```\n"
                "\n```xml\n<configuration><packageSourceCredentials /></configuration>\n```\n")

            self.assertEqual(mapping.shipped_package_ids(root), ["ApiPipeline.NET.Pkg"])
            configs = mapping.configurations(root)
            self.assertEqual([c.name for c in configs], [os.path.join("README.md") + ":3"])
            self.assertEqual(mapping.main(["check", root]), 0)


if __name__ == "__main__":
    unittest.main()
