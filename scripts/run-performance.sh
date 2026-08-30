#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
performance_home="${APIPIPELINE_PERF_HOME:-${repository_root}/.performance-local}"
run_count=5
job=""
active_pid=""

usage() {
  echo "Usage: scripts/run-performance.sh [--runs N] [--job JOB]"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --runs)
      run_count="$2"
      shift 2
      ;;
    --job)
      job="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if ! [[ "${run_count}" =~ ^[1-9][0-9]*$ ]]; then
  echo "--runs must be a positive integer" >&2
  exit 2
fi

dotnet_command="${DOTNET_CMD:-dotnet}"
dotnet_path="$(command -v "${dotnet_command}")"
dotnet_path="$(python3 -B - "${dotnet_path}" <<'PY'
import os
import sys

print(os.path.realpath(sys.argv[1]))
PY
)"
dotnet_directory="$(dirname "${dotnet_path}")"
export PATH="${dotnet_directory}:${PATH}"
if [[ -z "${DOTNET_ROOT:-}" ]]; then
  export DOTNET_ROOT="${dotnet_directory}"
fi

cleanup() {
  if [[ -n "${active_pid}" ]] && kill -0 "${active_pid}" 2>/dev/null; then
    kill -TERM "${active_pid}" 2>/dev/null || true
    wait "${active_pid}" 2>/dev/null || true
  fi
}

terminate() {
  cleanup
  exit 130
}

trap cleanup EXIT
trap terminate INT TERM

cd "${repository_root}"
docker info >/dev/null
"${dotnet_path}" build perf/ApiPipeline.NET.Perf/ApiPipeline.NET.Perf.csproj -c Release
benchmark_dll="${repository_root}/perf/ApiPipeline.NET.Perf/bin/Release/net10.0/ApiPipeline.NET.Perf.dll"
benchmark_job="${job:-Default}"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)-$$"
candidate_staging="${performance_home}/candidates/${timestamp}"
mkdir -p "${candidate_staging}/runs"

fingerprint_value() {
  local destination="$1"
  local cpu_model
  if [[ "$(uname -s)" == "Darwin" ]]; then
    cpu_model="$(sysctl -n machdep.cpu.brand_string 2>/dev/null || uname -p)"
  else
    cpu_model="$(awk -F: '/model name/{sub(/^[ \t]+/, "", $2); print $2; exit}' /proc/cpuinfo 2>/dev/null || uname -p)"
  fi
  local runtime
  runtime="$(python3 -B -m scripts.performance.runtime_probe \
    --dotnet "${dotnet_path}" --app "${benchmark_dll}")"
  local revision
  revision="$(git rev-parse HEAD)"
  if [[ -n "$(git status --porcelain --untracked-files=normal)" ]]; then
    revision="${revision}-dirty"
  fi
  python3 - "${destination}" "$(uname -srv)" "$(uname -m)" "${cpu_model}" \
    "$("${dotnet_path}" --version)" "${runtime}" \
    "$(docker version --format '{{.Server.Version}}')" "${revision}" \
    "${benchmark_job}" <<'PY'
import json
import sys

destination, os_name, architecture, cpu, sdk, runtime, docker, revision, job = sys.argv[1:]
with open(destination, "w", encoding="utf-8") as handle:
    json.dump(
        {
            "os": os_name,
            "architecture": architecture,
            "cpuModel": cpu,
            "dotnetSdk": sdk,
            "dotnetRuntime": runtime,
            "dockerServer": docker,
            "gitRevision": revision,
            "benchmarkJob": job,
        },
        handle,
        indent=2,
    )
    handle.write("\n")
PY
}

fingerprint_value "${candidate_staging}/fingerprint.json"
fingerprint_id="$(python3 -B - "${candidate_staging}/fingerprint.json" <<'PY'
import json
import sys

from scripts.performance.timing_summary import MachineFingerprint

with open(sys.argv[1], encoding="utf-8") as handle:
    print(MachineFingerprint.from_dict(json.load(handle)).identifier)
PY
)"
candidate="${performance_home}/candidates/${timestamp}-${fingerprint_id}"
mv "${candidate_staging}" "${candidate}"
accepted="${performance_home}/accepted/${fingerprint_id}"

for ((index = 1; index <= run_count; index++)); do
  run_directory="${candidate}/runs/$(printf '%02d' "${index}")"
  mkdir -p "${run_directory}"
  fingerprint_value "${run_directory}/fingerprint.json"
  benchmark_arguments=(--filter '*' --artifacts "${run_directory}")
  if [[ -n "${job}" ]]; then
    benchmark_arguments+=(--job "${job}")
  fi
  "${dotnet_path}" run -c Release --no-build --project perf/ApiPipeline.NET.Perf -- \
    "${benchmark_arguments[@]}" &
  active_pid="$!"
  wait "${active_pid}"
  active_pid=""
  python3 -B - "${run_directory}/results" <<'PY'
import sys
from pathlib import Path
from scripts.performance.benchmark_results import parse_reports

rows = parse_reports(Path(sys.argv[1]))
if not rows:
    raise SystemExit("benchmark run emitted no rows")
print(f"validated {len(rows)} benchmark rows")
PY
done

python3 -B scripts/summarize-benchmark-runs.py \
  --candidate "${candidate}" \
  --baseline "${accepted}"
echo "Performance candidate: ${candidate}"
echo "Accepted baseline: ${accepted}"
