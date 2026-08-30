#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
docker_command="${DOCKER_CMD:-docker}"
dotnet_command="${DOTNET_CMD:-dotnet}"
load_home="${APIPIPELINE_LOAD_HOME:-${repository_root}/.performance-local/load}"
output_directory=""
smoke=false
cleanup_probe=""
host_pid=""
active_container=""
active_client_pid=""

usage() {
  echo "Usage: scripts/run-load-tests.sh [--smoke] [--output DIRECTORY]"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --smoke)
      smoke=true
      shift
      ;;
    --output)
      output_directory="$2"
      shift 2
      ;;
    --cleanup-probe)
      cleanup_probe="$2"
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

cleanup_host() {
  if [[ -n "${host_pid}" ]] && kill -0 "${host_pid}" 2>/dev/null; then
    kill -TERM "${host_pid}" 2>/dev/null || true
    wait "${host_pid}" 2>/dev/null || true
  fi
  host_pid=""
}

cleanup_container() {
  if [[ -n "${active_container}" ]]; then
    "${docker_command}" stop "${active_container}" >/dev/null 2>&1 || true
  fi
  if [[ -n "${active_client_pid}" ]] && kill -0 "${active_client_pid}" 2>/dev/null; then
    kill -TERM "${active_client_pid}" 2>/dev/null || true
    wait "${active_client_pid}" 2>/dev/null || true
  fi
  active_container=""
  active_client_pid=""
}

cleanup() {
  cleanup_container
  cleanup_host
}

terminate() {
  exit 130
}

trap cleanup EXIT
trap terminate INT TERM

if [[ -n "${cleanup_probe}" ]]; then
  mkdir -p "${cleanup_probe}"
  (
    trap 'touch "'"${cleanup_probe}"'/host-terminated"; exit 0' TERM
    touch "${cleanup_probe}/ready"
    while true; do
      sleep 0.1
    done
  ) &
  host_pid="$!"
  active_container="probe-container"
  wait "${host_pid}"
  exit 0
fi

free_port() {
  python3 -B - <<'PY'
import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
}

wait_for_health() {
  local url="$1"
  local log_file="$2"
  for _ in {1..200}; do
    if curl --fail --silent --show-error "${url}/health" >/dev/null 2>&1; then
      return 0
    fi
    if ! kill -0 "${host_pid}" 2>/dev/null; then
      wait "${host_pid}" || true
      echo "Load-test host exited before readiness" >&2
      sed -n '1,240p' "${log_file}" >&2
      return 1
    fi
    sleep 0.1
  done
  echo "Timed out waiting for ${url}/health" >&2
  sed -n '1,240p' "${log_file}" >&2
  return 1
}

wait_for_labeled_cleanup() {
  local run_id="$1"
  for _ in {1..100}; do
    if [[ -z "$("${docker_command}" ps -a --quiet --filter "label=apipipeline.load-test.run=${run_id}")" ]]; then
      return 0
    fi
    sleep 0.1
  done
  echo "Container bearing run label ${run_id} remained after cleanup" >&2
  return 1
}

cd "${repository_root}"
"${docker_command}" info >/dev/null

if [[ -z "${output_directory}" ]]; then
  output_directory="${load_home}/$(date -u +%Y%m%dT%H%M%SZ)-$$"
fi
mkdir -p "${output_directory}/raw" "${output_directory}/hosts"
output_directory="$(cd "${output_directory}" && pwd)"
raw_directory="${output_directory}/raw"

k6_image="$(tr -d '[:space:]' < perf/load/k6-image.txt)"
if [[ "${k6_image}" != *@sha256:* ]]; then
  echo "k6 image must be pinned by digest" >&2
  exit 1
fi

"${dotnet_command}" build perf/ApiPipeline.NET.LoadTests/ApiPipeline.NET.LoadTests.csproj -c Release
host_dll="${repository_root}/perf/ApiPipeline.NET.LoadTests/bin/Release/net10.0/ApiPipeline.NET.LoadTests.dll"

warmup_duration="15s"
measure_duration="60s"
reject_duration="5s"
virtual_users="16"
miss_rate="50"
if [[ "${smoke}" == true ]]; then
  warmup_duration="1s"
  measure_duration="2s"
  reject_duration="1s"
  virtual_users="2"
  miss_rate="5"
fi

scenarios=(baseline pipeline redis cache-hit cache-miss redis-reject)
profiles=(baseline pipeline redis pipeline pipeline redis)
expected_statuses=(200 200 200 200 200 429)
campaign_failed=0

for index in "${!scenarios[@]}"; do
  scenario="${scenarios[$index]}"
  profile="${profiles[$index]}"
  expected_status="${expected_statuses[$index]}"
  port="$(free_port)"
  local_url="http://127.0.0.1:${port}"
  run_id="load-${scenario}-$$"
  host_log="${output_directory}/hosts/${scenario}.log"

  APIPIPELINE_LOAD_RUN_ID="${run_id}" \
    "${dotnet_command}" "${host_dll}" --profile "${profile}" --urls "${local_url}" \
      >"${host_log}" 2>&1 &
  host_pid="$!"
  wait_for_health "${local_url}" "${host_log}"

  active_container="apipipeline-k6-${scenario}-$$"
  docker_arguments=(
    run --rm --name "${active_container}"
    --label "apipipeline.load-test.run=${run_id}"
    --volume "${repository_root}/perf/load/k6:/scripts:ro"
    --volume "${raw_directory}:/results"
  )
  if [[ "$(uname -s)" == "Linux" ]]; then
    docker_arguments+=(--network host --add-host host.docker.internal:host-gateway)
    container_url="${local_url}"
  else
    container_url="http://host.docker.internal:${port}"
  fi
  docker_arguments+=(
    --env "BASE_URL=${container_url}"
    --env "SCENARIO=${scenario}"
    --env "EXPECTED_PROFILE=${profile}"
    --env "EXPECTED_STATUS=${expected_status}"
    --env "SUMMARY_PATH=/results/${scenario}.json"
    --env "WARMUP_DURATION=${warmup_duration}"
    --env "MEASURE_DURATION=${measure_duration}"
    --env "REJECT_DURATION=${reject_duration}"
    --env "VUS=${virtual_users}"
    --env "MISS_RATE=${miss_rate}"
    "${k6_image}" run /scripts/scenarios.js
  )

  "${docker_command}" "${docker_arguments[@]}" &
  active_client_pid="$!"
  if ! wait "${active_client_pid}"; then
    campaign_failed=1
  fi
  active_client_pid=""
  active_container=""
  cleanup_host
  wait_for_labeled_cleanup "${run_id}"

  if [[ ! -s "${raw_directory}/${scenario}.json" ]]; then
    echo "k6 did not produce ${scenario}.json" >&2
    exit 1
  fi
done

cpu_model="$(uname -p)"
if [[ "$(uname -s)" == "Darwin" ]]; then
  cpu_model="$(sysctl -n machdep.cpu.brand_string 2>/dev/null || uname -p)"
elif [[ -r /proc/cpuinfo ]]; then
  cpu_model="$(awk -F: '/model name/{sub(/^[ \t]+/, "", $2); print $2; exit}' /proc/cpuinfo)"
fi
runtime_version="$(python3 -B -m scripts.performance.runtime_probe \
  --dotnet "${dotnet_command}" --app "${host_dll}")"
git_revision="$(git rev-parse HEAD)"
if [[ -n "$(git status --porcelain --untracked-files=normal)" ]]; then
  git_revision="${git_revision}-dirty"
fi
python3 -B - "${output_directory}/metadata.json" \
  "$(uname -srv)" "$(uname -m)" "${cpu_model}" \
  "$("${dotnet_command}" --version)" "${runtime_version}" \
  "$("${docker_command}" version --format '{{.Server.Version}}')" \
  "${git_revision}" "${k6_image}" "${warmup_duration}" "${measure_duration}" \
  "${virtual_users}" "${miss_rate}" <<'PY'
import json
import sys

(
    destination,
    os_name,
    architecture,
    cpu,
    sdk,
    runtime,
    docker,
    revision,
    image,
    warmup,
    measure,
    vus,
    miss_rate,
) = sys.argv[1:]
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
            "k6Image": image,
            "warmupDuration": warmup,
            "measureDuration": measure,
            "virtualUsers": int(vus),
            "cacheMissRequestsPerSecond": int(miss_rate),
        },
        handle,
        indent=2,
    )
    handle.write("\n")
PY

summary_status=0
python3 -B -m scripts.performance.load_summary \
  --input-dir "${raw_directory}" \
  --metadata "${output_directory}/metadata.json" \
  --output-json "${output_directory}/load-summary.json" \
  --output-markdown "${output_directory}/load-summary.md" || summary_status="$?"

echo "Load-test artifacts: ${output_directory}"
if [[ "${campaign_failed}" -ne 0 || "${summary_status}" -ne 0 ]]; then
  exit 1
fi
