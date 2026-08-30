// Test classes run in parallel (xUnit's default). Every host owns its meter (IMeterFactory), and
// every MeterListener here filters on the meter's scope (see MetricRecorder), so a test hears only
// its own host. Keep it that way: a listener filtered by meter name alone also hears every other
// test running at the same time.
