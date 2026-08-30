using System.Net;
using System.Security.Cryptography;
using System.Text;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// How the rate limiter picks a request's bucket. Each test guards a bypass: a caller-controlled
/// key, or a key that does not change when the limit does.
/// </summary>
public sealed class RateLimitingPartitioningTests
{
    private static string HashOf(string apiKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)).AsSpan(0, 16));

    private static Dictionary<string, string?> RateLimitedConfig(
        int permitLimit,
        Action<Dictionary<string, string?>>? extra = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Kind"] = "FixedWindow";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] =
                permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:10:00";
            c["ApiPipeline:RateLimiting:Policies:0:QueueLimit"] = "0";
            extra?.Invoke(c);
        });

    /// <summary>
    /// The API key header is caller-supplied and unauthenticated. If an arbitrary value earned its
    /// own partition, a caller could mint a fresh bucket per request and never be limited at all —
    /// while growing the limiter's partition table without bound. Only allow-listed keys may
    /// partition; everything else falls through to the IP bucket.
    /// </summary>
    [Fact]
    public async Task RateLimiting_RotatingApiKeyHeader_CannotBypassLimit()
    {
        var config = RateLimitedConfig(2, c =>
        {
            c["ApiPipeline:RateLimiting:EnableApiKeyPartitioning"] = "true";
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:0"] = HashOf("legitimate-partner-key");
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/test");
            request.Headers.Add("X-Api-Key", $"attacker-forged-key-{i}");
            statuses.Add((await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        }

        statuses.Should().Contain((HttpStatusCode)429,
            "an unknown API key must not create a new rate limit partition — rotating the header " +
            "would otherwise bypass rate limiting entirely");
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(2,
            "every forged key shares the single fallback partition, so the configured PermitLimit applies");
    }

    /// <summary>
    /// A key the operator never allow-listed must be treated exactly like a request with no key:
    /// limited by IP alongside every other anonymous caller.
    /// </summary>
    [Fact]
    public async Task RateLimiting_UnauthenticatedApiKey_IsNotHonouredAsPartition()
    {
        var config = RateLimitedConfig(2, c =>
        {
            c["ApiPipeline:RateLimiting:EnableApiKeyPartitioning"] = "true";
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:0"] = HashOf("known-key");
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        // Two requests with no key at all consume the shared fallback bucket.
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        // An unknown key must land in that same exhausted bucket, not a fresh one.
        var unknown = new HttpRequestMessage(HttpMethod.Get, "/test");
        unknown.Headers.Add("X-Api-Key", "never-configured");
        (await client.SendAsync(unknown, TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429,
            "an unknown key shares the anonymous partition rather than creating its own");
    }

    /// <summary>
    /// An allow-listed key still gets the isolation the feature exists to provide, so the fix does
    /// not simply disable the capability.
    /// </summary>
    [Fact]
    public async Task RateLimiting_KnownApiKey_StillGetsItsOwnPartition()
    {
        var config = RateLimitedConfig(1, c =>
        {
            c["ApiPipeline:RateLimiting:EnableApiKeyPartitioning"] = "true";
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:0"] = HashOf("client-a");
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:1"] = HashOf("client-b");
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var a = new HttpRequestMessage(HttpMethod.Get, "/test");
        a.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(a, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var b = new HttpRequestMessage(HttpMethod.Get, "/test");
        b.Headers.Add("X-Api-Key", "client-b");
        (await client.SendAsync(b, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK,
            "an allow-listed key keeps its own bucket");

        var a2 = new HttpRequestMessage(HttpMethod.Get, "/test");
        a2.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(a2, TestContext.Current.CancellationToken)).StatusCode.Should().Be((HttpStatusCode)429,
            "client-a exhausted its own bucket");
    }

    /// <summary>
    /// Enabling API key partitioning without an allow-list is the unsafe configuration, so it must
    /// fail at startup rather than silently becoming a bypass.
    /// </summary>
    [Fact]
    public async Task RateLimiting_ApiKeyPartitioningWithoutAllowList_FailsStartup()
    {
        var config = RateLimitedConfig(5, c => c["ApiPipeline:RateLimiting:EnableApiKeyPartitioning"] = "true");

        var act = async () =>
        {
            await using var app = await TestAppBuilder.CreateAppAsync(config);
            await app.StartAsync();
        };

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .WithMessage("*KnownApiKeyHashes*");
    }

    /// <summary>
    /// An IPv6 end site is normally assigned a /64 or larger, so a single host owns every address
    /// inside it. Keying on the full /128 would hand that host 2^64 buckets.
    /// </summary>
    [Fact]
    public void RateLimiting_IPv6ClientsInSameSlash64_ShareAPartition()
    {
        var a = IPAddress.Parse("2001:db8:1:2::1");
        var b = IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd");

        RateLimitPartitionKeys.ForIp(a, 64, "strict:1")
            .Should().Be(RateLimitPartitionKeys.ForIp(b, 64, "strict:1"),
                "addresses inside one /64 belong to one client and must share a bucket");
    }

    /// <summary>
    /// Aggregation must not go so far that unrelated networks collide.
    /// </summary>
    [Fact]
    public void RateLimiting_IPv6ClientsInDifferentSlash64_DoNotSharePartition()
    {
        var a = IPAddress.Parse("2001:db8:1:2::1");
        var b = IPAddress.Parse("2001:db8:1:3::1");

        RateLimitPartitionKeys.ForIp(a, 64, "strict:1")
            .Should().NotBe(RateLimitPartitionKeys.ForIp(b, 64, "strict:1"));
    }

    /// <summary>
    /// A dual-stack listener reports the same peer as both a plain IPv4 address and its IPv4-mapped
    /// IPv6 form. Two partitions for one client would double its effective limit.
    /// </summary>
    [Fact]
    public void RateLimiting_IPv4MappedIPv6_SharesPartitionWithIPv4()
    {
        var mapped = IPAddress.Parse("::ffff:203.0.113.5");
        var plain = IPAddress.Parse("203.0.113.5");

        RateLimitPartitionKeys.ForIp(mapped, 64, "strict:1")
            .Should().Be(RateLimitPartitionKeys.ForIp(plain, 64, "strict:1"));
    }

    /// <summary>
    /// The prefix is configurable, and lowering it must widen the bucket.
    /// </summary>
    [Fact]
    public void RateLimiting_IPv6Prefix_IsConfigurable()
    {
        var a = IPAddress.Parse("2001:db8:1:2::1");
        var b = IPAddress.Parse("2001:db8:1:3::1");

        RateLimitPartitionKeys.ForIp(a, 48, "s").Should().Be(RateLimitPartitionKeys.ForIp(b, 48, "s"),
            "a /48 covers both /64s");
        RateLimitPartitionKeys.ForIp(a, 64, "s").Should().NotBe(RateLimitPartitionKeys.ForIp(b, 64, "s"));
    }

    /// <summary>
    /// IPv4 clients are always partitioned individually — the prefix setting is IPv6-only.
    /// </summary>
    [Fact]
    public void RateLimiting_IPv4Addresses_AreNotAggregated()
    {
        RateLimitPartitionKeys.ForIp(IPAddress.Parse("203.0.113.5"), 64, "s")
            .Should().NotBe(RateLimitPartitionKeys.ForIp(IPAddress.Parse("203.0.113.6"), 64, "s"));
    }

    /// <summary>
    /// The advertised limit and the enforced limit must be the same number. A header that reports a
    /// limit the limiter is not applying misleads every client pacing itself against it.
    /// </summary>
    [Fact]
    public async Task RateLimiting_XRateLimitLimitHeader_MatchesEnforcedLimit()
    {
        var initial = RateLimitedConfig(2);
        // This test reads the limit off a successful response, which is the opt-in behaviour.
        initial["ApiPipeline:RateLimiting:EmitRateLimitHeaders"] = "true";
        var source = new MemoryConfigurationSource { InitialData = initial };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        ((IConfigurationBuilder)builder.Configuration).Add(source);
        builder.Services.AddRouting();
        builder.Services
            .AddApiPipelineCorrelationId(builder.Configuration)
            .AddApiPipelineRateLimiting(builder.Configuration);

        await using var app = builder.Build();
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var first = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        var advertised = RateLimitHeaders.Quota(first)!.Value;
        advertised.Should().Be(2);

        var permitted = 1; // the request above
        for (var i = 0; i < 10; i++)
        {
            if ((await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode == HttpStatusCode.OK)
            {
                permitted++;
            }
        }

        permitted.Should().Be(advertised,
            "RateLimit-Policy quota must report the limit the limiter is actually enforcing");
    }

    /// <summary>
    /// The limiter must hold under parallel load, not just sequential requests.
    /// </summary>
    [Fact]
    public async Task RateLimiting_ConcurrentRequests_EnforceLimitExactly()
    {
        var config = RateLimitedConfig(10);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => client.GetAsync("/test")));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(10,
            "exactly PermitLimit requests may pass, however many arrive at once");
        responses.Count(r => (int)r.StatusCode == 429).Should().Be(90);
    }

    /// <summary>
    /// A rate-limit rejection must carry the same RFC 9457 body shape as every other error, so
    /// clients have one error contract to parse. The body comes from UseStatusCodePages.
    /// </summary>
    [Fact]
    public async Task RateLimiting_RejectedResponse_IsProblemJson()
    {
        var config = RateLimitedConfig(1);
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.UseConsumerErrorHandling();
        app.UseApiPipeline(pipeline => pipeline
            .WithCorrelationId()
            .WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        (await client.GetAsync("/test", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        ((int)rejected.StatusCode).Should().Be(429);
        rejected.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        rejected.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// The rejection metric must report the partition the request was actually placed in.
    /// </summary>
    [Fact]
    public async Task RateLimiting_RejectionTelemetry_ReportsApiKeyPartitionType()
    {
        var config = RateLimitedConfig(1, c =>
        {
            c["ApiPipeline:RateLimiting:EnableApiKeyPartitioning"] = "true";
            c["ApiPipeline:RateLimiting:KnownApiKeyHashes:0"] = HashOf("client-a");
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.Use(async (ctx, next) =>
        {
            // On starting: the 429 has a body, so the response has started once the limiter returns.
            ctx.Response.OnStarting(() =>
            {
                if (ctx.Response.StatusCode == 429)
                {
                    ctx.Response.Headers["X-Test-PartitionType"] =
                        ctx.Items[RateLimitContextItems.PartitionType]?.ToString();
                }

                return Task.CompletedTask;
            });
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var first = new HttpRequestMessage(HttpMethod.Get, "/test");
        first.Headers.Add("X-Api-Key", "client-a");
        (await client.SendAsync(first, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = new HttpRequestMessage(HttpMethod.Get, "/test");
        second.Headers.Add("X-Api-Key", "client-a");
        var rejected = await client.SendAsync(second, TestContext.Current.CancellationToken);

        ((int)rejected.StatusCode).Should().Be(429);
        rejected.Headers.GetValues("X-Test-PartitionType").Single().Should().Be("apikey");
    }

    /// <summary>
    /// The IPv6 prefix has a safe default rather than defaulting to the full address.
    /// </summary>
    [Fact]
    public void RateLimitingOptions_IPv6PartitionPrefixLength_DefaultsTo64()
    {
        new ApiPipelineRateLimitingOptions().IPv6PartitionPrefixLength.Should().Be(64);
    }
}
