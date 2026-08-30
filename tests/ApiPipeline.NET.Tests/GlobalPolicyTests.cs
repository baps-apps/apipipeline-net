using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>DefaultPolicy</c> is a fallback for endpoints without a policy; <c>GlobalPolicy</c> is
/// ASP.NET Core's <c>GlobalLimiter</c>: a ceiling on every endpoint, on top of its own policy.
/// </summary>
public sealed class GlobalPolicyTests
{
    private static Dictionary<string, string?> Config(string? globalPolicy, int strict = 1, int generous = 100, int ceiling = 3) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit";
            c["ApiPipeline:RateLimiting:EmitRateLimitHeadersOnRejection"] = "true";
            AddPolicy(c, 0, "strict", strict);
            AddPolicy(c, 1, "generous", generous);
            AddPolicy(c, 2, "ceiling", ceiling);
            if (globalPolicy is not null)
            {
                c["ApiPipeline:RateLimiting:GlobalPolicy"] = globalPolicy;
            }
        });

    private static void AddPolicy(Dictionary<string, string?> c, int i, string name, int limit)
    {
        c[$"ApiPipeline:RateLimiting:Policies:{i}:Name"] = name;
        c[$"ApiPipeline:RateLimiting:Policies:{i}:PermitLimit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        c[$"ApiPipeline:RateLimiting:Policies:{i}:Window"] = "00:10:00";
    }

    /// <summary><c>/default</c> has no policy of its own; <c>/generous</c> selects one.</summary>
    private static async Task<HttpClient> StartAsync(Dictionary<string, string?> config)
    {
        var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseRouting();
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/default", () => "ok");
        app.MapGet("/generous", () => "ok").RequireRateLimiting("generous");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app.GetTestClient();
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        await client.GetAsync(path, TestContext.Current.CancellationToken);

    /// <summary>Without <c>GlobalPolicy</c>, an endpoint's own policy is not capped by the default.</summary>
    [Fact]
    public async Task NoGlobalPolicy_EndpointPolicyIsNotCappedByDefault()
    {
        var client = await StartAsync(Config(globalPolicy: null, strict: 1));

        for (var i = 0; i < 5; i++)
        {
            (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await GetAsync(client, "/default")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/default")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the default allows 1");
    }

    /// <summary>
    /// <c>GlobalPolicy</c> caps every endpoint, the one with its own generous policy included, and
    /// the rejection names the global policy.
    /// </summary>
    [Fact]
    public async Task GlobalPolicy_CapsEveryEndpoint_OnTopOfItsOwnPolicy()
    {
        var client = await StartAsync(Config(globalPolicy: "ceiling", strict: 100, ceiling: 3));

        (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/default")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.OK);

        var rejected = await GetAsync(client, "/generous");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "3 requests across endpoints spent the global ceiling");
        rejected.Headers.GetValues("RateLimit-Policy").Single().Should().StartWith("\"ceiling\"");
        (await GetAsync(client, "/default")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>With a global ceiling, the default fallback still applies to endpoints without a policy.</summary>
    [Fact]
    public async Task GlobalPolicy_DefaultStillAppliesToEndpointsWithoutPolicy()
    {
        var client = await StartAsync(Config(globalPolicy: "ceiling", strict: 1, ceiling: 100));

        (await GetAsync(client, "/default")).StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = await GetAsync(client, "/default");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.GetValues("RateLimit-Policy").Single().Should().StartWith("\"strict\"");

        (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.OK, "the endpoint's own policy is not the default");
    }

    /// <summary>
    /// <c>GlobalPolicy</c> equal to <c>DefaultPolicy</c> is the framework's own semantics: the
    /// default caps every endpoint, and one bucket is spent once per request, not twice.
    /// </summary>
    [Fact]
    public async Task GlobalPolicyEqualToDefault_DefaultCapsEveryEndpoint()
    {
        var client = await StartAsync(Config(globalPolicy: "strict", strict: 2));

        (await GetAsync(client, "/default")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.OK, "the second permit of 2, not a double spend");
        (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// As with the framework's <c>GlobalLimiter</c>, a request the endpoint policy rejects still spends
    /// global permits (two in .NET 10: a synchronous then an asynchronous attempt). Pinned to catch a
    /// framework change.
    /// </summary>
    [Fact]
    public async Task GlobalPolicy_RequestRejectedByEndpointPolicy_StillSpendsGlobalPermits()
    {
        var client = await StartAsync(Config(globalPolicy: "ceiling", strict: 100, generous: 1, ceiling: 10));

        (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/generous")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var allowed = 0;
        for (var i = 0; i < 12; i++)
        {
            if ((await GetAsync(client, "/default")).StatusCode == HttpStatusCode.OK)
            {
                allowed++;
            }
        }

        allowed.Should().Be(7, "10 global permits, 1 for the success, 2 for the rejected request");
    }

    /// <summary>An unknown <c>GlobalPolicy</c> fails startup.</summary>
    [Fact]
    public void GlobalPolicy_Unknown_FailsValidation()
    {
        var options = new ApiPipelineRateLimitingOptions
        {
            Enabled = true,
            Policies = [new ApiPipelineRateLimitPolicy { Name = "strict", PermitLimit = 1, Window = TimeSpan.FromSeconds(60) }],
            GlobalPolicy = "missing",
        };

        var result = new ApiPipelineRateLimitingOptionsValidator().Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("GlobalPolicy 'missing'");
    }
}
