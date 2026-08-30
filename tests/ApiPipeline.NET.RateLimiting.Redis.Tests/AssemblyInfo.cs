using ApiPipeline.NET.RateLimiting.Redis.Tests;
using Xunit.Sdk;
using Xunit.v3;

// Serial, unlike the core suite. The hosts share one Redis container, and one test pauses it to
// simulate an outage, which must not overlap with tests that expect Redis to answer. The window,
// refill and lease-expiry tests also run on the Redis server's clock (TIME inside the scripts),
// which a test cannot fake, so they need the machine quiet enough to keep a 1 s window. Timeouts,
// cooldowns and renewal timers on the client side run on FakeTimeProvider wherever a test asserts
// on them, so no test asserts that something finished faster than a wall-clock limit.
[assembly: Parallelization(Mode = ParallelMode.None)]

// One Redis container for the whole assembly; each test isolates itself with its own KeyPrefix.
[assembly: AssemblyFixture(typeof(RedisFixture))]

// Every test here needs Docker. The trait lets a run select or exclude them
// (--filter-not-trait "Category=Docker"); without Docker a local run skips them with the reason
// rather than failing all of them, and CI fails (see RedisFixture).
[assembly: Trait("Category", "Docker")]
[assembly: SkipWithoutDocker]
