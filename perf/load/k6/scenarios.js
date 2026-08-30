import execution from "k6/execution";
import http from "k6/http";
import { check, fail } from "k6";
import { Counter } from "k6/metrics";
import { buildSummary } from "./summary.js";

const expectedStatuses = new Counter("expected_statuses");
const unexpectedStatuses = new Counter("unexpected_statuses");
const profileMismatches = new Counter("profile_mismatches");
const status200 = new Counter("status_200");
const status429 = new Counter("status_429");

const scenario = __ENV.SCENARIO;
const profile = __ENV.EXPECTED_PROFILE;
const expectedStatus = Number(__ENV.EXPECTED_STATUS || "200");
const baseUrl = __ENV.BASE_URL;
const warmupDuration = __ENV.WARMUP_DURATION || "15s";
const measureDuration = __ENV.MEASURE_DURATION || "60s";
const rejectDuration = __ENV.REJECT_DURATION || "5s";
const virtualUsers = Number(__ENV.VUS || "16");
const missRate = Number(__ENV.MISS_RATE || "50");

if (!baseUrl || !scenario || !profile) {
  throw new Error("BASE_URL, SCENARIO, and EXPECTED_PROFILE are required");
}

function constantVusScenarios() {
  return {
    warmup: {
      executor: "constant-vus",
      vus: virtualUsers,
      duration: warmupDuration,
      exec: "warmup",
      tags: { phase: "warmup" },
    },
    measure: {
      executor: "constant-vus",
      vus: virtualUsers,
      duration: measureDuration,
      startTime: warmupDuration,
      exec: "measure",
      tags: { phase: "measure" },
    },
  };
}

function cacheMissScenarios() {
  const allocated = Math.max(virtualUsers, Math.ceil(missRate / 2));
  return {
    warmup: {
      executor: "constant-arrival-rate",
      rate: missRate,
      timeUnit: "1s",
      duration: warmupDuration,
      preAllocatedVUs: allocated,
      maxVUs: allocated * 2,
      exec: "warmup",
      tags: { phase: "warmup" },
    },
    measure: {
      executor: "constant-arrival-rate",
      rate: missRate,
      timeUnit: "1s",
      duration: measureDuration,
      startTime: warmupDuration,
      preAllocatedVUs: allocated,
      maxVUs: allocated * 2,
      exec: "measure",
      tags: { phase: "measure" },
    },
  };
}

export const options = {
  discardResponseBodies: true,
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
  scenarios:
    scenario === "redis-reject"
      ? {
          measure: {
            executor: "constant-vus",
            vus: 1,
            duration: rejectDuration,
            exec: "measure",
            tags: { phase: "measure" },
          },
        }
      : scenario === "cache-miss"
        ? cacheMissScenarios()
        : constantVusScenarios(),
  thresholds: {
    checks: ["rate==1"],
    "checks{phase:measure}": ["rate==1"],
    http_req_failed: ["rate==0"],
    "http_req_failed{phase:measure}": ["rate==0"],
    "http_reqs{phase:measure}": ["count>0"],
    "http_req_duration{phase:measure}": ["p(99)>=0"],
    expected_statuses: ["count>0"],
    unexpected_statuses: ["count==0"],
    profile_mismatches: ["count==0"],
  },
};

function requestPath(phase) {
  if (scenario === "cache-hit") {
    return "/cached?key=warmed";
  }
  if (scenario === "cache-miss") {
    return `/cached?key=${phase}-${execution.vu.idInTest}-${execution.scenario.iterationInTest}`;
  }
  if (scenario === "redis-reject") {
    return "/reject";
  }
  return "/payload";
}

function send(phase) {
  const response = http.get(`${baseUrl}${requestPath(phase)}`, {
    tags: { phase },
    responseCallback: http.expectedStatuses(expectedStatus),
  });
  const statusMatches = response.status === expectedStatus;
  const profileMatches = response.headers["X-Apipipeline-Load-Profile"] === profile;
  check(
    response,
    {
      [`status is ${expectedStatus}`]: () => statusMatches,
      "profile header matches": () => profileMatches,
    },
    { phase },
  );

  if (phase === "measure") {
    expectedStatuses.add(statusMatches ? 1 : 0);
    unexpectedStatuses.add(statusMatches ? 0 : 1);
    profileMismatches.add(profileMatches ? 0 : 1);
    status200.add(response.status === 200 ? 1 : 0);
    status429.add(response.status === 429 ? 1 : 0);
  }
}

export function setup() {
  if (scenario !== "redis-reject") {
    return;
  }
  const response = http.get(`${baseUrl}/reject`, {
    responseCallback: http.expectedStatuses(200),
  });
  if (
    response.status !== 200 ||
    response.headers["X-Apipipeline-Load-Profile"] !== profile
  ) {
    fail("Redis rejection setup did not consume exactly one permit");
  }
}

export function warmup() {
  send("warmup");
}

export function measure() {
  send("measure");
}

export function handleSummary(data) {
  return buildSummary(data);
}
