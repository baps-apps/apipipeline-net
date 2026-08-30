using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for configuration that is dangerous, contradictory, or silently ignored.
/// A shared infrastructure package should fail fast rather than run in a state the operator
/// did not intend.
/// </summary>
public sealed class ConfigurationSafetyTests
{
    /// <summary>
    /// <c>ApiPipeline:OutputCaching:Enabled</c> must take effect from configuration alone. Requiring a
    /// second, code-only opt-in means an operator can set the flag, see nothing happen, and have no
    /// signal explaining why.
    /// </summary>
    [Fact]
    public async Task OutputCaching_EnabledInConfiguration_IsHonouredWithoutCodeOptIn()
    {
        var config = TestAppBuilder.MinimalConfig(c => c["ApiPipeline:OutputCaching:Enabled"] = "true");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddRouting();
        builder.Services.AddApiPipeline(builder.Configuration);
        await using var app = builder.Build();

        app.Services.GetRequiredService<IOptions<ApiPipelineOutputCachingOptions>>()
            .Value.Enabled.Should().BeTrue();
    }

    /// <summary>
    /// Response caching and output caching solve the same problem with different semantics. Running
    /// both stores the same response twice and makes cache behaviour depend on pipeline position,
    /// which nobody can reason about — so it must be rejected at startup.
    /// </summary>
    [Fact]
    public async Task Caching_ResponseAndOutputCaching_BothEnabled_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCaching:Enabled"] = "true";
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*OutputCaching*");
    }

    /// <summary>
    /// Either one alone must still start.
    /// </summary>
    [Fact]
    public async Task Caching_OnlyOutputCaching_Enabled_StartsSuccessfully()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:ResponseCaching:Enabled"] = "false";
            c["ApiPipeline:OutputCaching:Enabled"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// An API key is a credential. Using it verbatim as a rate limiter partition key keeps the
    /// plaintext secret alive in a long-lived dictionary and puts it one log statement away from
    /// disclosure. The key must be derived, not copied.
    /// </summary>
    [Fact]
    public void RateLimitPartitionKeys_ForApiKey_DoesNotContainTheRawKey()
    {
        const string ApiKey = "sk-live-super-secret-value";

        var partitionKey = RateLimitPartitionKeys.ForApiKey(ApiKey, "strict:0a1b2c3d");

        partitionKey.Should().NotContain(ApiKey);
        partitionKey.Should().NotContain("super-secret");
    }

    /// <summary>
    /// Derivation must be stable, or every request would land in its own partition and rate
    /// limiting would never trigger.
    /// </summary>
    [Fact]
    public void RateLimitPartitionKeys_ForApiKey_IsStableForTheSameKey()
    {
        RateLimitPartitionKeys.ForApiKey("abc", "strict:0a1b2c3d").Should().Be(RateLimitPartitionKeys.ForApiKey("abc", "strict:0a1b2c3d"));
    }

    /// <summary>
    /// Different keys must land in different partitions, otherwise one client's traffic throttles
    /// another's.
    /// </summary>
    [Fact]
    public void RateLimitPartitionKeys_ForApiKey_DiffersBetweenKeys()
    {
        RateLimitPartitionKeys.ForApiKey("abc", "strict:0a1b2c3d").Should().NotBe(RateLimitPartitionKeys.ForApiKey("abd", "strict:0a1b2c3d"));
    }

    /// <summary>
    /// The rate limiter hashes an incoming API key once, to check it against the allow-list, and
    /// reuses that hash for the partition key rather than running SHA-256 a second time on the
    /// request path. Both spellings must therefore produce the identical key — if they diverge, a
    /// known key silently lands in a different partition than the one the allow-list admitted.
    /// </summary>
    [Fact]
    public void RateLimitPartitionKeys_ForApiKeyHash_MatchesForApiKey()
    {
        const string ApiKey = "sk-live-super-secret-value";
        const string Scope = "strict:0a1b2c3d";
        var hash = RateLimitPartitionKeys.HashApiKey(ApiKey);

        RateLimitPartitionKeys.ForApiKeyHash(hash, Scope)
            .Should().Be(RateLimitPartitionKeys.ForApiKey(ApiKey, Scope));
    }
}
