#!/usr/bin/env bash
# Publishes tests/ApiPipeline.NET.AotSmoke as a Native AOT app and runs it.
#
# Publishing checks the IsAotCompatible claim of every package against what the trimmer and the AOT
# compiler see, dependencies included: any IL2xxx/IL3xxx warning fails the publish
# (TreatWarningsAsErrors). Running the native binary checks the features still work once trimmed:
# configuration binding and options validation must not need reflection.
#
# Checks, against the native binary:
#   - headers: correlation, security, deprecation and CORS;
#   - rate limiting: a two-per-minute endpoint answers 200, 200, then a 429 with its headers;
#   - shared rate limiting: two instances on one Redis share that limit. Needs Docker; without it a
#     local run skips this part, and CI (CI=true) fails instead.
#
# Usage: scripts/aot-smoke.sh [output-directory]   (needs the platform's native toolchain: clang on
# Linux, Xcode command line tools on macOS). Listens on 127.0.0.1, ports APIPIPELINE_AOT_SMOKE_PORT
# (default 5089) to that plus 2; a port something else already serves fails the run.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$root/artifacts/aot-smoke}"
port="${APIPIPELINE_AOT_SMOKE_PORT:-5089}"
redis_ports=($((port + 1)) $((port + 2)))

dotnet publish "$root/tests/ApiPipeline.NET.AotSmoke" -c Release -o "$out" -nologo

app="$out/ApiPipeline.NET.AotSmoke"
pids=()
logs=()
container=""

cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do
    kill "$pid" 2>/dev/null || true
  done
  if [ -n "$container" ]; then
    docker stop "$container" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

fail=0
expect() {   # expect <description> <headers> <pattern>
  if grep -qi -- "$3" <<<"$2"; then echo "ok:   $1"; else echo "::error::missing: $1 ($3)"; fail=1; fi
}

expect_statuses() {   # expect_statuses <description> <expected> <url>...
  local description=$1 expected=$2 actual=""
  shift 2
  for url in "$@"; do
    actual+="$(curl -s -o /dev/null -w '%{http_code}' "$url") "
  done
  actual=${actual% }
  if [ "$actual" = "$expected" ]; then
    echo "ok:   $description ($actual)"
  else
    echo "::error::$description: expected $expected, got $actual"
    fail=1
  fi
}

show_logs_and_exit() {
  for log in "${logs[@]}"; do
    echo "--- $log"; cat "$log"
  done
  exit 1
}

start_app() {   # start_app <port> [NAME=value]...
  local app_port=$1
  shift
  local log="$out/smoke-$app_port.log"

  # Otherwise the checks below would run against whatever already answers there.
  if curl -s -o /dev/null --max-time 1 "http://127.0.0.1:$app_port/"; then
    echo "::error::Port $app_port is already in use; free it or set APIPIPELINE_AOT_SMOKE_PORT."
    exit 1
  fi

  # exec: the background PID is the app itself, so cleanup stops it rather than only the subshell.
  (cd "$out" && exec env ASPNETCORE_URLS="http://127.0.0.1:$app_port" ASPNETCORE_ENVIRONMENT=Production "$@" \
    "$app" >"$log" 2>&1) &
  pids+=($!)
  logs+=("$log")

  local pid=$!
  for _ in $(seq 1 50); do
    curl -fs "http://127.0.0.1:$app_port/health" >/dev/null 2>&1 && return 0
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "::error::The AOT app on port $app_port exited at startup:"; show_logs_and_exit
    fi
    sleep 0.2
  done
  echo "::error::The AOT app on port $app_port did not answer /health"; show_logs_and_exit
}

start_app "$port"
base="http://127.0.0.1:$port"

health=$(curl -fsS -D - -o /dev/null "$base/health")
expect "correlation id"            "$health" '^x-correlation-id: '
expect "security headers"          "$health" '^x-content-type-options: nosniff'
expect "cross-origin resource policy" "$health" '^cross-origin-resource-policy: same-origin'
expect "content security policy"   "$health" "^content-security-policy: default-src 'none'"

v1=$(curl -fsS -D - -o /dev/null "$base/api/v1/orders")
expect "deprecation (RFC 9745)"    "$v1" '^deprecation: @'
expect "sunset (RFC 8594)"         "$v1" '^sunset: '
expect "deprecation link"          "$v1" 'rel="deprecation"'
expect "sunset link"               "$v1" 'rel="sunset"'

cors=$(curl -fsS -D - -o /dev/null -H 'Origin: https://app.example.com' "$base/health")
expect "CORS from configuration"   "$cors" '^access-control-allow-origin: https://app.example.com'

expect_statuses "rate limit (2 per minute)" "200 200" "$base/limited" "$base/limited"
rejected=$(curl -s -D - "$base/limited")
expect "429 status"                "$rejected" '^HTTP/[0-9.]* 429'
expect "429 retry-after"           "$rejected" '^retry-after: [1-9]'
expect "429 problem details"       "$rejected" '^content-type: application/problem+json'
expect "429 correlation id"        "$rejected" '^x-correlation-id: '
expect "429 security headers"      "$rejected" '^x-content-type-options: nosniff'

# Shared limits: the same two-per-minute policy, counted in Redis by two instances. A third request
# is rejected only if the instances share the count; the keys in Redis show it was Redis that
# counted. The image is the one the Redis tests pin.
if docker info >/dev/null 2>&1; then
  image=$(sed -n '/const string Redis =/{n;p;}' \
    "$root/tests/ApiPipeline.NET.RateLimiting.Redis.Tests/TestImages.cs" | grep -o 'redis:[^"]*')
  container=$(docker run -d --rm -p 127.0.0.1::6379 "$image")
  redis_port=$(docker port "$container" 6379/tcp | head -n 1 | sed 's/.*://')
  for _ in $(seq 1 50); do
    [ "$(docker exec "$container" redis-cli ping 2>/dev/null)" = "PONG" ] && break
    sleep 0.2
  done

  secret=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
  # CommandTimeout (appsettings.json) is 2 s, so the first request waits for the connection
  # instead of being decided by the per-pod fallback.
  for redis_app_port in "${redis_ports[@]}"; do
    start_app "$redis_app_port" \
      ApiPipeline__RateLimiting__Redis__Enabled=true \
      ApiPipeline__RateLimiting__Redis__Configuration="127.0.0.1:$redis_port" \
      ApiPipeline__RateLimiting__Redis__KeyPrefix=aot-smoke \
      ApiPipeline__RateLimiting__Redis__PartitionKeySecret="$secret"
  done

  a="http://127.0.0.1:${redis_ports[0]}/limited"
  b="http://127.0.0.1:${redis_ports[1]}/limited"
  expect_statuses "shared rate limit across instances (Redis)" "200 200 429 429" "$a" "$b" "$a" "$b"

  keys=$(docker exec "$container" redis-cli --scan --pattern 'aot-smoke:*' | wc -l | tr -d ' ')
  if [ "$keys" -gt 0 ]; then
    echo "ok:   rate-limit keys in Redis ($keys)"
  else
    echo "::error::no aot-smoke:* keys in Redis: the instances did not count in Redis"
    fail=1
  fi
elif [ "${CI:-}" = "true" ]; then
  echo "::error::Docker is not available; the shared (Redis) rate limiting check needs it on CI."
  fail=1
else
  echo "skip: shared (Redis) rate limiting: Docker is not available. Start Docker to run it."
fi

if [ "$fail" -ne 0 ]; then
  show_logs_and_exit
fi
echo "Native AOT smoke test passed."
