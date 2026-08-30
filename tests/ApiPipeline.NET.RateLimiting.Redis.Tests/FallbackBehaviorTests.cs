using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// What a policy does while Redis cannot decide. Per-pod limits (the default) multiply the
/// allowance by the replica count, which is wrong for a login, OTP or payment policy: those fail
/// closed with <see cref="RedisFallbackBehavior.Reject"/>, set for every policy or per policy.
/// </summary>
public sealed class FallbackBehaviorTests
{
    private const string Unreachable = "127.0.0.1:1,connectTimeout=200";

    private static async Task<List<HttpStatusCode>> RequestsAsync(Dictionary<string, string?> config, int count)
    {
        await using var app = await RedisTestHost.CreateAsync(Unreachable, config);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            statuses.Add(await app.GetStatusAsync());
        }

        return statuses;
    }

    private static Task<WebApplication> NeverConnectsAsync(Dictionary<string, string?> config, FakeTimeProvider clock) =>
        RedisTestHost.CreateAsync(
            Unreachable,
            config,
            configureServices: (services, configuration) =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddApiPipelineRedisRateLimiting(configuration, _ => throw new RedisException("test: never connects"));
            });

    /// <summary>Sends one request, which must be rejected, and returns its <c>Retry-After</c>.</summary>
    private static async Task<string?> RetryAfterAsync(WebApplication app)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri("/test", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        return response.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null;
    }

    /// <summary>Fail closed: with Redis down, nothing passes, even within the limit.</summary>
    [Fact]
    public async Task Reject_WhileRedisIsDown_RejectsEveryRequest()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Redis:Fallback"] = "Reject";

        var statuses = await RequestsAsync(config, 3);

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// A fail-closed 429 during an outage says to come back after a cooldown, so clients do not
    /// retry into a rejection every second. A <c>Reject</c> policy asks Redis on every request
    /// (it is not gated by the circuit), so each failed call starts a new cooldown, and each 429
    /// says so. The clock is fake and stands still between steps.
    /// </summary>
    [Fact]
    public async Task Reject_WhileRedisIsDown_SendsTheCooldownAsRetryAfter()
    {
        var clock = new FakeTimeProvider();
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Redis:Fallback"] = "Reject";
        config["ApiPipeline:RateLimiting:Redis:FallbackCooldown"] = "00:00:30";
        await using var app = await NeverConnectsAsync(config, clock);

        (await RetryAfterAsync(app)).Should().Be("30", "the failure opened the circuit for a full cooldown");

        clock.Advance(TimeSpan.FromSeconds(12));
        (await RetryAfterAsync(app)).Should().Be("30", "this request asked Redis too, and its failure started a new cooldown");
    }

    /// <summary>
    /// Below the failure threshold the circuit is closed and the next request asks Redis again, so
    /// the 429 says to come back in a second rather than nothing (retry at once).
    /// </summary>
    [Fact]
    public async Task Reject_BelowTheFailureThreshold_SendsOneSecondRetryAfter()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Redis:Fallback"] = "Reject";
        config["ApiPipeline:RateLimiting:Redis:FailureThreshold"] = "5";
        await using var app = await NeverConnectsAsync(config, new FakeTimeProvider());

        (await RetryAfterAsync(app)).Should().Be("1");
        app.Services.GetRequiredService<RedisCircuit>().IsOpen.Should().BeFalse();
    }

    /// <summary>Fail open: with Redis down, the policy does not limit at all.</summary>
    [Fact]
    public async Task Allow_WhileRedisIsDown_AdmitsEveryRequest()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 2);
        config["ApiPipeline:RateLimiting:Redis:Fallback"] = "Allow";

        var statuses = await RequestsAsync(config, 5);

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.OK);
    }

    /// <summary>A per-policy setting applies where the default does not say so.</summary>
    [Fact]
    public async Task PolicyFallback_Reject_OverridesThePerPodDefault()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Redis:PolicyFallbacks:strict"] = "Reject";

        var statuses = await RequestsAsync(config, 3);

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.TooManyRequests);
    }

    /// <summary>A per-policy setting wins over the default, in either direction, and ignores case.</summary>
    [Fact]
    public async Task PolicyFallback_OverridesTheDefault_IgnoringCase()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 2);
        config["ApiPipeline:RateLimiting:Redis:Fallback"] = "Reject";
        config["ApiPipeline:RateLimiting:Redis:PolicyFallbacks:STRICT"] = "Allow";

        var statuses = await RequestsAsync(config, 5);

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// A misspelled policy name would leave the real policy on the default, failing open where
    /// closed was meant, so a name that matches no policy fails startup.
    /// </summary>
    [Fact]
    public async Task PolicyFallback_ForUnknownPolicy_FailsStartup()
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config["ApiPipeline:RateLimiting:Redis:PolicyFallbacks:login"] = "Reject";

        var act = async () => await RedisTestHost.CreateAsync(Unreachable, config);

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("'login'");
    }

    /// <summary>
    /// Configuration binds an enum from its number too, so <c>"3"</c> binds to a value with no
    /// member, which every <c>switch</c> treats as <see cref="RedisFallbackBehavior.PerPod"/>: an
    /// operator who meant to fail closed would fail open. A value that is no member fails startup.
    /// </summary>
    [Theory]
    [InlineData("ApiPipeline:RateLimiting:Redis:Fallback", "Fallback")]
    [InlineData("ApiPipeline:RateLimiting:Redis:PolicyFallbacks:strict", "PolicyFallbacks")]
    public async Task UndefinedFallbackValue_FailsStartup(string key, string setting)
    {
        var config = RedisTestHost.FixedWindow(RedisTestHost.UniquePrefix(), permitLimit: 5);
        config[key] = "3";

        var act = async () => await RedisTestHost.CreateAsync(Unreachable, config);

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain(setting).And.Contain("PerPod, Allow, Reject");
    }

    /// <summary>The default stays per pod: no one is blocked, and no one is unlimited.</summary>
    [Fact]
    public void Options_DefaultToPerPod_WithNoPolicyOverrides()
    {
        var options = new ApiPipelineRedisRateLimitingOptions();

        options.Fallback.Should().Be(RedisFallbackBehavior.PerPod);
        options.PolicyFallbacks.Should().BeEmpty();
    }
}
