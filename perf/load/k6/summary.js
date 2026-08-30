function durationSeconds(value) {
  const match = /^(\d+(?:\.\d+)?)(ms|s|m|h)$/.exec(value || "");
  if (!match) {
    throw new Error(`Unsupported k6 duration '${value}'`);
  }
  const factors = { ms: 0.001, s: 1, m: 60, h: 3600 };
  return Number(match[1]) * factors[match[2]];
}

export function buildSummary(data) {
  const measuredDuration =
    __ENV.SCENARIO === "redis-reject"
      ? __ENV.REJECT_DURATION || "5s"
      : __ENV.MEASURE_DURATION || "60s";
  data.load = {
    scenario: __ENV.SCENARIO,
    profile: __ENV.EXPECTED_PROFILE,
    expectedStatus: Number(__ENV.EXPECTED_STATUS || "200"),
    measureDurationSeconds: durationSeconds(measuredDuration),
  };
  const destination = __ENV.SUMMARY_PATH || `/results/${__ENV.SCENARIO}.json`;
  return { [destination]: JSON.stringify(data, null, 2) };
}
