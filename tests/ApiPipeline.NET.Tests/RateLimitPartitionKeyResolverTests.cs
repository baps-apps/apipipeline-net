using System.Net;
using System.Security.Claims;
using ApiPipeline.NET.Pipeline;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Custom partitioning: <c>PartitionClaims</c> and <see cref="IRateLimitPartitionKeyResolver"/>
/// choose the bucket ahead of the built-in user → API key → IP chain, so an enterprise can limit per
/// tenant or per OAuth client.
/// </summary>
public sealed class RateLimitPartitionKeyResolverTests
{
    private const string SubHeader = "X-Test-Sub";
    private const string TenantHeader = "X-Test-Tenant";
    private const string ClientHeader = "X-Test-Client";

    private static Dictionary<string, string?> Config(int permitLimit, Action<Dictionary<string, string?>>? extra = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:AnonymousFallback"] = "RateLimit";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] =
                permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:10:00";
            extra?.Invoke(c);
        });

    /// <summary>
    /// An app whose caller identity comes from test headers: <c>X-Test-Sub</c> (the user),
    /// <c>X-Test-Tenant</c> and <c>X-Test-Client</c> become claims of an authenticated identity, or of
    /// an unauthenticated one when <c>X-Test-Anonymous</c> is present.
    /// </summary>
    private static async Task<HttpClient> StartAsync(
        Dictionary<string, string?> config,
        Action<IServiceCollection>? services = null)
    {
        var app = await TestAppBuilder.CreateAppAsync(config, configureServices: services);
        app.Use(async (ctx, next) =>
        {
            var claims = new List<Claim>();
            if (ctx.Request.Headers[SubHeader].FirstOrDefault() is { } sub)
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
            }

            if (ctx.Request.Headers[TenantHeader].FirstOrDefault() is { } tenant)
            {
                claims.Add(new Claim("tenant_id", tenant));
            }

            if (ctx.Request.Headers[ClientHeader].FirstOrDefault() is { } client)
            {
                claims.Add(new Claim("azp", client));
            }

            var authenticationType = ctx.Request.Headers.ContainsKey("X-Test-Anonymous") ? null : "Test";
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
            await next(ctx);
        });
        app.UseApiPipeline(pipeline => pipeline.WithRateLimiting());
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app.GetTestClient();
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    // ------------------------------------------------------------------ PartitionClaims

    /// <summary>Two users of one tenant share the tenant's bucket.</summary>
    [Fact]
    public async Task PartitionClaims_UsersOfOneTenant_ShareOneBucket()
    {
        var client = await StartAsync(Config(2, c => c["ApiPipeline:RateLimiting:PartitionClaims:0"] = "tenant_id"));

        (await GetAsync(client, (SubHeader, "alice"), (TenantHeader, "contoso"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "bob"), (TenantHeader, "contoso"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "carol"), (TenantHeader, "contoso"))).Should().Be(HttpStatusCode.TooManyRequests,
            "the tenant's bucket of 2 is spent, whichever user asks");
        (await GetAsync(client, (SubHeader, "dave"), (TenantHeader, "fabrikam"))).Should().Be(HttpStatusCode.OK,
            "another tenant has its own bucket");
    }

    /// <summary>The control: without <c>PartitionClaims</c>, the same users each get a bucket.</summary>
    [Fact]
    public async Task WithoutPartitionClaims_EachUserHasOwnBucket()
    {
        var client = await StartAsync(Config(1));

        (await GetAsync(client, (SubHeader, "alice"), (TenantHeader, "contoso"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "bob"), (TenantHeader, "contoso"))).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>The claims are tried in order, and a caller with none of them falls back to its user id.</summary>
    [Fact]
    public async Task PartitionClaims_InOrder_FallBackToUser()
    {
        var client = await StartAsync(Config(1, c =>
        {
            c["ApiPipeline:RateLimiting:PartitionClaims:0"] = "tenant_id";
            c["ApiPipeline:RateLimiting:PartitionClaims:1"] = "azp";
        }));

        // No tenant: the OAuth client decides, so two users of one client share a bucket.
        (await GetAsync(client, (SubHeader, "alice"), (ClientHeader, "mobile-app"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "bob"), (ClientHeader, "mobile-app"))).Should().Be(HttpStatusCode.TooManyRequests);

        // Neither claim: per user.
        (await GetAsync(client, (SubHeader, "carol"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "dave"))).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Claims of an unauthenticated identity are not trusted: that caller is limited by IP (here
    /// the shared anonymous bucket), not by a tenant it claims.
    /// </summary>
    [Fact]
    public async Task PartitionClaims_IgnoredWhenNotAuthenticated()
    {
        var client = await StartAsync(Config(1, c => c["ApiPipeline:RateLimiting:PartitionClaims:0"] = "tenant_id"));

        (await GetAsync(client, ("X-Test-Anonymous", "1"), (TenantHeader, "a"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, ("X-Test-Anonymous", "1"), (TenantHeader, "b"))).Should().Be(HttpStatusCode.TooManyRequests,
            "a claimed tenant must not mint a new bucket for an unauthenticated caller");
    }

    /// <summary>An empty entry in <c>PartitionClaims</c> fails startup.</summary>
    [Fact]
    public void PartitionClaims_EmptyEntry_FailsValidation()
    {
        var options = new ApiPipelineRateLimitingOptions
        {
            Enabled = true,
            Policies = [new ApiPipelineRateLimitPolicy { Name = "strict", PermitLimit = 1, Window = TimeSpan.FromSeconds(60) }],
            PartitionClaims = ["tenant_id", " "],
        };

        new ApiPipelineRateLimitingOptionsValidator().Validate(Options.DefaultName, options)
            .Failed.Should().BeTrue();
    }

    // ------------------------------------------------------------------ IRateLimitPartitionKeyResolver

    /// <summary>A registered resolver decides ahead of everything, including <c>PartitionClaims</c>.</summary>
    [Fact]
    public async Task Resolver_DecidesBeforeClaimsAndUser()
    {
        var client = await StartAsync(
            Config(1, c => c["ApiPipeline:RateLimiting:PartitionClaims:0"] = "tenant_id"),
            services => services.AddApiPipelineRateLimitPartitionKeyResolver<ClientResolver>());

        // Different tenants and users, one OAuth client: the resolver puts them in one bucket.
        (await GetAsync(client, (SubHeader, "alice"), (TenantHeader, "contoso"), (ClientHeader, "batch"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "bob"), (TenantHeader, "fabrikam"), (ClientHeader, "batch"))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>A resolver that returns null leaves the decision to the next, then the built-in chain.</summary>
    [Fact]
    public async Task Resolver_ReturningNull_FallsThrough()
    {
        var client = await StartAsync(
            Config(1),
            services => services
                .AddApiPipelineRateLimitPartitionKeyResolver<NeverResolver>()
                .AddApiPipelineRateLimitPartitionKeyResolver<ClientResolver>());

        // No client claim: both resolvers pass, so the user id decides.
        (await GetAsync(client, (SubHeader, "alice"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "bob"))).Should().Be(HttpStatusCode.OK);

        // With a client claim: the second resolver decides.
        (await GetAsync(client, (SubHeader, "carol"), (ClientHeader, "batch"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "dave"), (ClientHeader, "batch"))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// A resolver that throws is the application's bug, but it must not turn every rate-limited
    /// request into a 500: it is skipped and logged, and the next resolver, then the built-in
    /// chain, decide. Here the user id does: two users pass, a user's second request is limited.
    /// </summary>
    [Fact]
    public async Task Resolver_Throwing_IsSkipped_AndLogged()
    {
        var logs = new CapturingLoggerProvider();
        var client = await StartAsync(
            Config(1),
            services => services
                .AddLogging(logging => logging.AddProvider(logs))
                .AddApiPipelineRateLimitPartitionKeyResolver<ThrowingResolver>());

        (await GetAsync(client, (SubHeader, "alice"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "bob"))).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, (SubHeader, "alice"))).Should().Be(HttpStatusCode.TooManyRequests);

        lock (logs.Messages)
        {
            logs.Messages.Should().Contain(m => m.Contains(nameof(ThrowingResolver), StringComparison.Ordinal)
                && m.Contains("'strict'", StringComparison.Ordinal));
        }
    }

    /// <summary>Adding the same resolver type twice registers it once.</summary>
    [Fact]
    public void Resolver_AddedTwice_RegisteredOnce()
    {
        var services = new ServiceCollection()
            .AddApiPipelineRateLimitPartitionKeyResolver<ClientResolver>()
            .AddApiPipelineRateLimitPartitionKeyResolver<ClientResolver>();

        services.Count(d => d.ServiceType == typeof(IRateLimitPartitionKeyResolver)).Should().Be(1);
    }

    // ------------------------------------------------------------------ keys

    /// <summary>The partition type is a metric attribute, so it is restricted to a small token.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Tenant")]
    [InlineData("tenant:id")]
    [InlineData("a-very-long-partition-type-name-x")]
    public void PartitionKey_InvalidType_Throws(string type)
    {
        var act = () => new RateLimitPartitionKey(type, "value");

        act.Should().Throw<ArgumentException>().WithParameterName("type");
    }

    /// <summary>An empty value is not a partition.</summary>
    [Fact]
    public void PartitionKey_EmptyValue_Throws()
    {
        var act = () => new RateLimitPartitionKey("tenant", " ");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Keys from different sources never collide: a resolver's, a claim's and a user's key for the
    /// same text are distinct, and a <c>:</c> inside a claim type cannot shift where its value starts.
    /// </summary>
    [Fact]
    public void Keys_AreNamespaced_AndUnambiguous()
    {
        var resolved = RateLimitPartitionKeys.ForResolved(new RateLimitPartitionKey("user", "alice"), "s");
        var user = RateLimitPartitionKeys.ForUser("alice", "s");
        var claim = RateLimitPartitionKeys.ForClaim("user", "alice", "s");

        new[] { resolved, user, claim }.Should().OnlyHaveUniqueItems();
        RateLimitPartitionKeys.ForClaim("a:b", "c", "s")
            .Should().NotBe(RateLimitPartitionKeys.ForClaim("a", "b:c", "s"));
    }

    /// <summary>A long value, or one containing <c>|</c>, is hashed rather than stored as is.</summary>
    [Theory]
    [InlineData("tenant|other-policy")]
    [InlineData("line\nbreak")]
    public void Keys_UnsafeValue_IsHashed(string value)
    {
        RateLimitPartitionKeys.SafeValue(value).Should().StartWith("h:").And.HaveLength(34);
        RateLimitPartitionKeys.SafeValue(new string('x', 500)).Should().StartWith("h:").And.HaveLength(34);
        RateLimitPartitionKeys.SafeValue("contoso").Should().Be("contoso");
    }

    /// <summary>
    /// A user id is request-derived like a claim value, so it gets the same treatment: one containing
    /// <c>|</c> cannot shift where the policy scope starts, and an oversized one is not stored as is.
    /// </summary>
    [Theory]
    [InlineData("alice|strict:0000000000000000")]
    [InlineData("line\nbreak")]
    public void UserKey_UnsafeId_IsHashed(string userId)
    {
        var key = RateLimitPartitionKeys.ForUser(userId, "s");

        key.Should().Be($"user:{RateLimitPartitionKeys.SafeValue(userId)}|s");
        key.Count(c => c == '|').Should().Be(1, "only the separator before the scope");
        RateLimitPartitionKeys.ForUser(new string('x', 500), "s").Should().HaveLength("user:h:".Length + 32 + "|s".Length);
        RateLimitPartitionKeys.ForUser("alice", "s").Should().Be("user:alice|s", "a plain id keeps its key");
    }

    /// <summary>
    /// There is no partition key without a valid type: <c>default</c> is not a key (it is
    /// <c>null</c>, "not mine"), so no instance can skip the validating constructor.
    /// </summary>
    [Fact]
    public void PartitionKey_Default_IsNotAKey()
    {
        object? key = default(RateLimitPartitionKey);

        key.Should().BeNull();
    }

    /// <summary>Partitions on the <c>azp</c> claim, as a resolver an application would write.</summary>
    private sealed class ClientResolver : IRateLimitPartitionKeyResolver
    {
        public RateLimitPartitionKey? Resolve(HttpContext context, ApiPipelineRateLimitPolicy policy) =>
            context.User.FindFirst("azp")?.Value is { } client ? new RateLimitPartitionKey("client", client) : null;
    }

    /// <summary>A resolver with a bug.</summary>
    private sealed class ThrowingResolver : IRateLimitPartitionKeyResolver
    {
        public RateLimitPartitionKey? Resolve(HttpContext context, ApiPipelineRateLimitPolicy policy) =>
            throw new InvalidOperationException("test: resolver bug");
    }

    /// <summary>Never decides.</summary>
    private sealed class NeverResolver : IRateLimitPartitionKeyResolver
    {
        public RateLimitPartitionKey? Resolve(HttpContext context, ApiPipelineRateLimitPolicy policy) => null;
    }
}
