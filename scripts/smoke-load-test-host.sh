#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="${DOTNET_CMD:-dotnet}"
project="${repository_root}/perf/ApiPipeline.NET.LoadTests/ApiPipeline.NET.LoadTests.csproj"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/apipipeline-load-smoke.XXXXXX")"
host_pid=""

cleanup_host() {
  if [[ -n "${host_pid}" ]] && kill -0 "${host_pid}" 2>/dev/null; then
    kill -TERM "${host_pid}" 2>/dev/null || true
    wait "${host_pid}" 2>/dev/null || true
  fi
  host_pid=""
}

cleanup() {
  cleanup_host
  rm -rf "${temporary_directory}"
}

trap cleanup EXIT
trap 'exit 130' INT TERM

free_port() {
  python3 -B - <<'PY'
import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
}

wait_for_health() {
  local base_url="$1"
  local log_file="$2"
  for _ in {1..100}; do
    if curl --fail --silent --show-error "${base_url}/health" >/dev/null 2>&1; then
      return 0
    fi
    if ! kill -0 "${host_pid}" 2>/dev/null; then
      wait "${host_pid}" || true
      echo "Load-test host exited before becoming healthy" >&2
      sed -n '1,240p' "${log_file}" >&2
      return 1
    fi
    sleep 0.1
  done
  echo "Timed out waiting for ${base_url}/health" >&2
  sed -n '1,240p' "${log_file}" >&2
  return 1
}

assert_success() {
  local url="$1"
  curl --fail --silent --show-error "${url}" >/dev/null
}

cd "${repository_root}"
docker info >/dev/null
"${dotnet_command}" build "${project}" -c Release

invalid_port="$(free_port)"
if "${dotnet_command}" run --no-build -c Release --project "${project}" -- \
  --profile invalid --urls "http://127.0.0.1:${invalid_port}" \
  >"${temporary_directory}/invalid.log" 2>&1; then
  echo "Invalid load-test profile unexpectedly succeeded" >&2
  exit 1
fi
if curl --silent --max-time 1 "http://127.0.0.1:${invalid_port}/health" >/dev/null 2>&1; then
  echo "Invalid load-test profile unexpectedly started Kestrel" >&2
  exit 1
fi

for profile in baseline pipeline redis; do
  port="$(free_port)"
  base_url="http://127.0.0.1:${port}"
  log_file="${temporary_directory}/${profile}.log"
  run_id="smoke-${profile}-$$"
  APIPIPELINE_LOAD_RUN_ID="${run_id}" \
    "${dotnet_command}" run --no-build -c Release --project "${project}" -- \
      --profile "${profile}" --urls "${base_url}" >"${log_file}" 2>&1 &
  host_pid="$!"
  wait_for_health "${base_url}" "${log_file}"

  curl --fail --silent --show-error --dump-header "${temporary_directory}/headers" \
    --output "${temporary_directory}/payload" "${base_url}/payload"
  actual_profile="$(awk 'BEGIN { IGNORECASE=1 } /^X-ApiPipeline-Load-Profile:/ { print $2 }' \
    "${temporary_directory}/headers" | tr -d '\r')"
  if [[ "${actual_profile}" != "${profile}" ]]; then
    echo "Expected profile header ${profile}, found ${actual_profile:-<missing>}" >&2
    exit 1
  fi
  if [[ "$(wc -c <"${temporary_directory}/payload" | tr -d ' ')" != "1024" ]]; then
    echo "${profile} payload was not exactly 1 KB" >&2
    exit 1
  fi

  assert_success "${base_url}/cached"
  assert_success "${base_url}/cached"

  first_reject_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    "${base_url}/reject")"
  second_reject_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    "${base_url}/reject")"
  if [[ "${profile}" == "baseline" ]]; then
    expected_reject_status="200"
  else
    expected_reject_status="429"
    if [[ "${first_reject_status}" != "200" ]]; then
      echo "${profile} rejection policy rejected its first request" >&2
      exit 1
    fi
  fi
  if [[ "${second_reject_status}" != "${expected_reject_status}" ]]; then
    echo "${profile} second rejection request returned ${second_reject_status}, expected ${expected_reject_status}" >&2
    exit 1
  fi
  cleanup_host

  if [[ "${profile}" == "redis" ]]; then
    for _ in {1..50}; do
      if [[ -z "$(docker ps -a --quiet --filter "label=apipipeline.load-test.run=${run_id}")" ]]; then
        break
      fi
      sleep 0.1
    done
    if [[ -n "$(docker ps -a --quiet --filter "label=apipipeline.load-test.run=${run_id}")" ]]; then
      echo "Redis container bearing run label ${run_id} remained after shutdown" >&2
      exit 1
    fi
  fi
done

echo "Load-test host smoke checks passed."
