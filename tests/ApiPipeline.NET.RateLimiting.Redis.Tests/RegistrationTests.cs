using System.Net;
using System.Threading.RateLimiting;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Registration, binding and startup validation. A misconfiguration here either fails startup with
/// a message that says what to fix, or silently leaves every limit per pod; the tests pin the first.
/// </summary>
public sealed class RegistrationTests(RedisFixture redis)
{
    private const string Unreachable = "127.0.0.1:1";

    private static ApiPipelineRedisRateLimitingOptions Bind(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddApiPipelineRedisRateLimiting(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ApiPipelineRedisRateLimitingOptions>>().Value;
    }

    /// <summary>
    /// The core package's rule applies here too: passing the rate limiting or Redis section itself
    /// would read <c>...:RateLimiting:Redis</c> beneath it, bind nothing and leave limits per pod, so
    /// it throws, naming the section and what to pass instead.
    /// </summary>
    [Theory]
    [InlineData("ApiPipeline:RateLimiting")]
    [InlineData("ApiPipeline:RateLimiting:Redis")]
    [InlineData("apipipeline:ratelimiting:redis")]
    public void FeatureOwnSection_Throws(string path)
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => new ServiceCollection().AddApiPipelineRedisRateLimiting(configuration.GetSection(path));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("configuration")
            .WithMessage($"*'{path}' section itself*GetSection(\"ApiPipeline\")*");
    }

    /// <summary>
    /// A second call with a different section would be ignored; it throws instead, naming both.
    /// The same section again stays a no-op.
    /// </summary>
    [Fact]
    public void DifferentSection_Throws_SameSection_DoesNot()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddApiPipelineRedisRateLimiting(configuration);

        services.Invoking(s => s.AddApiPipelineRedisRateLimiting(configuration)).Should().NotThrow();
        services.Invoking(s => s.AddApiPipelineRedisRateLimiting(configuration.GetSection("Tenant")))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*'ApiPipeline:RateLimiting:Redis'*'Tenant:RateLimiting:Redis'*");
    }

    /// <summary>
    /// The settings live beside the policies they share, at <c>ApiPipeline:RateLimiting:Redis</c>,
    /// and every one of them binds.
    /// </summary>
    [Fact]
    public void Options_BindFrom_ApiPipeline_RateLimiting_Redis()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiPipeline:RateLimiting:Redis:Enabled"] = "true",
                ["ApiPipeline:RateLimiting:Redis:Configuration"] = "redis.internal:6379",
                ["ApiPipeline:RateLimiting:Redis:KeyPrefix"] = "orders",
                ["ApiPipeline:RateLimiting:Redis:CommandTimeout"] = "00:00:00.500",
                ["ApiPipeline:RateLimiting:Redis:FallbackCooldown"] = "00:00:10",
                ["ApiPipeline:RateLimiting:Redis:ConcurrencyLeaseTimeout"] = "00:02:00",
            })
            .Build();

        var options = Bind(configuration);

        options.Enabled.Should().BeTrue();
        options.Configuration.Should().Be("redis.internal:6379");
        options.KeyPrefix.Should().Be("orders");
        options.CommandTimeout.Should().Be(TimeSpan.FromMilliseconds(500));
        options.FallbackCooldown.Should().Be(TimeSpan.FromSeconds(10));
        options.ConcurrencyLeaseTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// Same rule as every other ApiPipeline registration: a section is the pipeline section itself,
    /// so an app that keeps its pipeline under <c>Platform:Api</c> finds the Redis settings there.
    /// </summary>
    [Fact]
    public void Options_FromSection_ReadAsThePipelineSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:Api:RateLimiting:Redis:KeyPrefix"] = "from-section",
                ["ApiPipeline:RateLimiting:Redis:KeyPrefix"] = "from-root",
            })
            .Build();

        Bind(configuration.GetSection("Platform:Api")).KeyPrefix.Should().Be("from-section");
    }

    /// <summary>
    /// Without a configured prefix, keys are scoped to the application and environment, so staging
    /// never spends production's quota and two APIs never share a bucket.
    /// </summary>
    [Fact]
    public void KeyPrefix_Default_IsApplicationAndEnvironment()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "api-a",
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Redis:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:Redis:Configuration"] = Unreachable,
        });
        builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
        using var app = builder.Build();

        var factory = app.Services.GetRequiredService<IApiPipelineRateLimiterFactory>();

        factory.Should().BeOfType<RedisRateLimiterFactory>()
            .Which.KeyPrefix.Should().Be("api-a:Development");
    }

    /// <summary>
    /// Enabled with nowhere to connect is a deployment mistake. Failing startup, with every way to
    /// fix it named, beats running with limits that are silently per pod.
    /// </summary>
    [Fact]
    public async Task Enabled_WithNoConnectionSource_FailsStartup()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Redis:Configuration"] = null;

        var start = () => RedisTestHost.CreateAsync(redis.ConnectionString, config);

        var failure = await start.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should()
            .Contain("AddApiPipelineRedisRateLimiting(configuration, sp =>")
            .And.Contain("ApiPipeline:RateLimiting:Redis:Configuration")
            .And.Contain("IConnectionMultiplexer");
    }

    /// <summary>
    /// A shared limiter admits or rejects; it cannot park a request in a queue that spans pods.
    /// Ignoring a configured queue would change behavior silently, so startup fails instead.
    /// </summary>
    [Fact]
    public async Task Enabled_WithQueueLimit_FailsStartup()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "2";

        var start = () => RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("QueueLimit must be 0");
    }

    /// <summary>
    /// More segments than milliseconds in the window would floor the segment length to 0 ms and make
    /// the sliding window script divide by zero on every request. Startup fails instead (on the
    /// segment cap, which such a count always exceeds).
    /// </summary>
    [Fact]
    public async Task Enabled_WithSubMillisecondSegments_FailsStartup()
    {
        var config = RedisTestHost.SlidingWindow(RedisTestHost.UniquePrefix(), permitLimit: 5, windowSeconds: 1, segmentsPerWindow: 1001);

        var start = () => RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("SegmentsPerWindow must be at most 100");
    }

    /// <summary>
    /// The script measures segments in whole milliseconds. A window that does not divide evenly
    /// (1000 ms in 3) would be cut to whole segments (3 × 333 ms = 999 ms); with 600 segments, 1000 ms
    /// became 600 ms and admitted 1.67 times the limit. Startup fails instead, naming the policy.
    /// </summary>
    [Fact]
    public async Task Enabled_WithWindowNotDividingIntoSegments_FailsStartup()
    {
        var config = RedisTestHost.SlidingWindow(RedisTestHost.UniquePrefix(), permitLimit: 5, windowSeconds: 1, segmentsPerWindow: 3);

        var start = () => RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("'strict'").And.Contain("divide evenly");
    }

    /// <summary>
    /// The script reads every segment on every request, so the count is capped: 120 segments divide
    /// an hour evenly, and are still refused.
    /// </summary>
    [Fact]
    public async Task Enabled_WithTooManySegments_FailsStartup()
    {
        var config = RedisTestHost.SlidingWindow(RedisTestHost.UniquePrefix(), permitLimit: 5, windowSeconds: 3600, segmentsPerWindow: 120);

        var start = () => RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("'strict'").And.Contain("SegmentsPerWindow must be at most 100");
    }

    /// <summary>The largest allowed count, dividing the shortest window evenly (10 ms each), starts.</summary>
    [Fact]
    public async Task Enabled_WithHundredEvenSegments_Starts()
    {
        var config = RedisTestHost.SlidingWindow(RedisTestHost.UniquePrefix(), permitLimit: 5, windowSeconds: 1, segmentsPerWindow: 100);

        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await app.GetStatusAsync()).Should().Be(System.Net.HttpStatusCode.OK);
    }

    /// <summary>
    /// A prefix with whitespace is almost always a paste error, and an operator debugging keys in
    /// <c>redis-cli</c> would trip over it. Rejected at startup, naming the setting.
    /// </summary>
    [Fact]
    public async Task KeyPrefix_WithWhitespace_FailsStartup()
    {
        var config = RedisTestHost.FixedWindow("api a", permitLimit: 5);

        var start = () => RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("KeyPrefix");
    }

    /// <summary>
    /// Braces in the prefix would replace the key's Redis Cluster hash tag and put every key in one
    /// slot: rejected at startup.
    /// </summary>
    [Theory]
    [InlineData("api{a}")]
    [InlineData("{orders}")]
    public async Task KeyPrefix_WithBraces_FailsStartup(string prefix)
    {
        var config = RedisTestHost.FixedWindow(prefix, permitLimit: 5);

        var start = () => RedisTestHost.CreateAsync(redis.ConnectionString, config);

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("must not contain '{' or '}'");
    }

    /// <summary>
    /// Outside Development the prefix must be set: the fallback is the assembly name, often generic,
    /// so two APIs sharing a Redis could share buckets.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task KeyPrefix_Unset_OutsideDevelopment_FailsStartup(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Redis:Enabled"] = "true",
            ["ApiPipeline:RateLimiting:Redis:Configuration"] = Unreachable,
        });
        builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);
        await using var app = builder.Build();

        var start = async () => await app.StartAsync(TestContext.Current.CancellationToken);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("KeyPrefix is required outside Development").And.Contain(environment);
    }

    /// <summary>
    /// Installing the package is not opting in: with <c>Enabled: false</c> every limit is per pod,
    /// exactly as without the package, and Redis is never contacted.
    /// </summary>
    [Fact]
    public async Task Disabled_UsesPerPodLimits_AndNeverConnects()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 2, redisEnabled: false);
        await using var hostA = await RedisTestHost.CreateAsync(Unreachable, config);
        await using var hostB = await RedisTestHost.CreateAsync(Unreachable, config);

        var statuses = new List<HttpStatusCode>
        {
            await hostA.GetStatusAsync(),
            await hostA.GetStatusAsync(),
            await hostB.GetStatusAsync(),
            await hostB.GetStatusAsync(),
        };

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.OK, "each pod has its own bucket of 2");
        hostA.Services.GetRequiredService<RedisConnectionSource>().ConnectionRequested.Should().BeFalse();
        hostB.Services.GetRequiredService<RedisConnectionSource>().ConnectionRequested.Should().BeFalse();
    }

    /// <summary>
    /// A connection passed in code is the most explicit choice, so it wins over a connection string
    /// and over a multiplexer that merely happens to be in DI (both unreachable here). Two hosts
    /// sharing the limit proves the explicit connection was used.
    /// </summary>
    [Fact]
    public async Task ExplicitConnection_WinsOverConfigurationAndDi()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 2);

        void UseExplicitConnection(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect($"{Unreachable},abortConnect=false,connectTimeout=100"));
            services.AddApiPipelineRedisRateLimiting(configuration, _ => redis.Multiplexer);
        }

        await using var hostA = await RedisTestHost.CreateAsync(Unreachable, config, configureServices: UseExplicitConnection);
        await using var hostB = await RedisTestHost.CreateAsync(Unreachable, config, configureServices: UseExplicitConnection);

        var statuses = new List<HttpStatusCode>
        {
            await hostA.GetStatusAsync(),
            await hostA.GetStatusAsync(),
            await hostB.GetStatusAsync(),
            await hostB.GetStatusAsync(),
        };

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(2, "the explicit connection shares one bucket");
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().Be(2);
    }

    /// <summary>
    /// A blocking connection factory does not block requests: each acquire is bounded by the
    /// command timeout and decided per pod, and concurrent acquires are not queued behind each other.
    /// </summary>
    // Hang guard: a factory run on the caller's thread would block forever.
    [Fact(Timeout = 30_000)]
    public async Task BlockingConnectionFactory_IsBoundedByCommandTimeout()
    {
        var clock = new FakeTimeProvider();
        using var connecting = new ManualResetEventSlim();
        var (provider, limiter) = RedisTestHost.CreateLimiter(
            _ =>
            {
                connecting.Wait();
                throw new InvalidOperationException("simulated connect failure");
            },
            timeProvider: clock);
        try
        {
            await using var _ = provider;
            using var __ = limiter;

            var acquires = Enumerable.Range(0, 4)
                .Select(_ => limiter.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask())
                .ToArray();

            acquires.Should().OnlyContain(a => !a.IsCompleted, "each waits on Redis until the command timeout");

            clock.Advance(TimeSpan.FromMilliseconds(250));

            // Real time only guards against a hang; the assertion is that one timeout decided all four.
            foreach (var lease in await Task.WhenAll(acquires).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
            {
                lease.IsAcquired.Should().BeTrue("a timed-out acquire is decided by the per-pod limiter");
                lease.Dispose();
            }
        }
        finally
        {
            connecting.Set();
        }
    }

    /// <summary>
    /// A connection factory that returns null is a wiring mistake. It must not fail requests: the
    /// acquire is decided per pod, and the one Warning says which registration to fix rather than
    /// surfacing later as a NullReferenceException.
    /// </summary>
    [Fact]
    public async Task ExplicitConnection_ReturningNull_FallsBack_AndNamesTheRegistration()
    {
        var logs = new CapturingLoggerProvider();
        var (provider, limiter) = RedisTestHost.CreateLimiter(_ => null!, logs);
        await using var _ = provider;
        using var __ = limiter;

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        lease.IsAcquired.Should().BeTrue("the per-pod limiter decides");
        provider.GetRequiredService<RedisCircuit>().IsOpen.Should().BeTrue();
        logs.Messages.Should().ContainSingle(m => m.Contains(RedisCircuit.UnavailableMessage, StringComparison.Ordinal))
            .Which.Should().Contain("AddApiPipelineRedisRateLimiting(configuration, sp => ...)");
    }

    /// <summary>
    /// A connection string StackExchange.Redis cannot parse (a misspelt option, a value of the
    /// wrong type) would fail every connect. Startup fails instead, naming the setting but not
    /// echoing its value, which may hold the password.
    /// </summary>
    [Theory]
    [InlineData("redis:6379,pasword=s3cr3t-pw")]
    [InlineData("redis:6379,password=s3cr3t-pw,connectTimeout=abc")]
    [InlineData("redis:6379,password=s3cr3t-pw,ssl=maybe")]
    [InlineData("password=s3cr3t-pw")]
    public async Task Enabled_WithMalformedConfiguration_FailsStartup_WithoutTheValue(string connectionString)
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);

        var start = () => RedisTestHost.CreateAsync(connectionString, config);

        var failure = (await start.Should().ThrowAsync<OptionsValidationException>()).Which;
        failure.Message.Should().Contain("ApiPipeline:RateLimiting:Redis:Configuration");
        failure.ToString().Should().NotContain("s3cr3t-pw");
    }

    /// <summary>
    /// A malformed connection string is only an error when sharing is on: with
    /// <c>Enabled: false</c> it is never used, so it does not fail startup.
    /// </summary>
    [Fact]
    public async Task Disabled_WithMalformedConfiguration_Starts()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5, redisEnabled: false);

        await using var app = await RedisTestHost.CreateAsync("redis:6379,pasword=x", config);

        (await app.GetStatusAsync()).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Registration order does not matter: the add-on registered before <c>AddApiPipeline</c> still
    /// replaces the in-memory factory, so two hosts spend one bucket (5 of 8 pass, not 8).
    /// </summary>
    [Fact]
    public async Task RegisteredBeforeAddApiPipeline_StillShares()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        await using var hostA = await RedisTestHost.CreateAsync(redis.ConnectionString, config, registerRedisFirst: true);
        await using var hostB = await RedisTestHost.CreateAsync(redis.ConnectionString, config, registerRedisFirst: true);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await hostA.GetStatusAsync());
            statuses.Add(await hostB.GetStatusAsync());
        }

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(5, "both hosts spend one shared bucket");
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().Be(3);
    }
    /// <summary>An unsupported whole-number duration key fails validation, naming its replacement.</summary>
    [Theory]
    [InlineData("CommandTimeoutMilliseconds", "CommandTimeout")]
    [InlineData("FallbackCooldownSeconds", "FallbackCooldown")]
    [InlineData("SamplingDurationSeconds", "SamplingDuration")]
    [InlineData("ConcurrencyLeaseTimeoutSeconds", "ConcurrencyLeaseTimeout")]
    public void FormerDurationKey_FailsValidation(string former, string replacement)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"ApiPipeline:RateLimiting:Redis:{former}"] = "30" })
            .Build();

        var bind = () => Bind(configuration);

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage($"*'ApiPipeline:RateLimiting:Redis:{former}' is not read*the setting is '{replacement}'*");
    }

    /// <summary>A bare number is read as days, so it fails validation with the duration to write.</summary>
    [Theory]
    [InlineData("FallbackCooldown", "30", "\"00:00:30\" for 30 seconds")]
    [InlineData("ConcurrencyLeaseMaxDuration", "2", "\"00:00:02\" for 2 seconds")]
    public void BareNumberDuration_FailsValidation(string key, string value, string suggestion)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"ApiPipeline:RateLimiting:Redis:{key}"] = value })
            .Build();

        var bind = () => Bind(configuration);

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage($"*'ApiPipeline:RateLimiting:Redis:{key}' is '{value}', which reads as {value} days*{suggestion}*");
    }

    /// <summary>
    /// Shared limits serve the core package's rate limiter. With <c>ApiPipeline:RateLimiting</c> off
    /// there is nothing to share, so starting the host must not open a Redis connection.
    /// </summary>
    [Fact]
    public async Task CoreRateLimitingOff_DoesNotConnect()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Enabled"] = "false";

        await using var app = await RedisTestHost.CreateAsync(redis.ConnectionString, config);

        app.Services.GetRequiredService<RedisConnectionSource>().ConnectionRequested.Should().BeFalse();
    }

    private sealed class CustomFactory : IApiPipelineRateLimiterFactory
    {
        private readonly InMemoryRateLimiterFactory _inner = new();

        public RateLimiter Create(string partitionKey, ApiPipelineRateLimitPolicy policy) => _inner.Create(partitionKey, policy);
    }

    /// <summary>
    /// An application's own factory, registered before the add-on, would be silently replaced, so
    /// its limiters would never run. Registration throws instead, naming the factory, however it
    /// was registered.
    /// </summary>
    [Theory]
    [InlineData("type")]
    [InlineData("instance")]
    [InlineData("delegate")]
    public void CustomFactory_RegisteredBefore_Throws(string form)
    {
        var services = new ServiceCollection();
        _ = form switch
        {
            "type" => services.AddSingleton<IApiPipelineRateLimiterFactory, CustomFactory>(),
            "instance" => services.AddSingleton<IApiPipelineRateLimiterFactory>(new CustomFactory()),
            _ => services.AddSingleton<IApiPipelineRateLimiterFactory>(_ => new CustomFactory()),
        };

        services.Invoking(s => s.AddApiPipelineRedisRateLimiting(new ConfigurationBuilder().Build()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*IApiPipelineRateLimiterFactory*AddApiPipelineRedisRateLimiting*");
    }

    /// <summary>
    /// The core package's default factory, registered explicitly, is not a conflict: the add-on
    /// replaces it as it replaces the one <c>AddApiPipeline</c> registers.
    /// </summary>
    [Fact]
    public void InMemoryFactory_RegisteredBefore_IsReplaced()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IApiPipelineRateLimiterFactory>(new InMemoryRateLimiterFactory());

        services.AddApiPipelineRedisRateLimiting(new ConfigurationBuilder().Build());

        services.Where(d => d.ServiceType == typeof(IApiPipelineRateLimiterFactory))
            .Should().ContainSingle()
            .Which.ImplementationType.Should().Be<RedisRateLimiterFactory>();
    }

    /// <summary>
    /// An application's own factory registered after the add-on wins resolution, so no policy would
    /// be shared through Redis. Startup fails, naming the factory, whether or not sharing is enabled
    /// (the conflict is in code, and must not surface only when an operator turns sharing on).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomFactory_RegisteredAfter_FailsStartup(bool redisEnabled)
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5, redisEnabled: redisEnabled);

        var start = () => RedisTestHost.CreateAsync(
            redis.ConnectionString,
            config,
            configureServices: (s, _) => s.AddSingleton<IApiPipelineRateLimiterFactory, CustomFactory>());

        var failure = await start.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should()
            .Contain(nameof(CustomFactory))
            .And.Contain("AddApiPipelineRedisRateLimiting");
    }
}
