using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The defaults are the contract this package is actually consumed under: thirty applications will
/// take whatever an empty configuration section produces. Each assertion here is a deliberate
/// posture decision, so a change to any of them has to be an argued change rather than a typo.
/// </summary>
public sealed class SecureDefaultsTests
{
    /// <summary>
    /// Security headers apply as soon as the middleware is in the pipeline. The middleware only
    /// runs when the application asked for it, so an absent configuration section producing no
    /// headers at all would be a trap rather than a safe default.
    /// </summary>
    [Fact]
    public void SecurityHeaders_AreOnByDefault()
    {
        new ApiPipelineSecurityHeadersOptions().Enabled.Should().BeTrue();
    }

    /// <summary>
    /// With <c>WithSecurityHeaders()</c> in the pipeline and no <c>ApiPipeline:SecurityHeaders</c>
    /// section bound at all, the API-relevant headers must still be present on a response.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_AreApplied_WithNoConfigurationSectionAtAll()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c.Remove("ApiPipeline:SecurityHeaders:Enabled");
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.UseApiPipeline(p => p.WithSecurityHeaders());
        app.MapGet("/ok", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync("/ok", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        response.Headers.Contains("X-Frame-Options").Should().BeTrue();
        response.Headers.Contains("Referrer-Policy").Should().BeTrue();
        response.Headers.GetValues("Content-Security-Policy").Should()
            .ContainSingle().Which.Should().Be("default-src 'none'; frame-ancestors 'none'");
    }

    /// <summary>
    /// Compression over HTTPS stays opt-in: it is the configuration that exposes BREACH/CRIME to an
    /// API that mixes reflected input with secrets in one response.
    /// </summary>
    [Fact]
    public void ResponseCompression_OverHttps_IsOptIn()
    {
        new ApiPipelineResponseCompressionOptions().EnableForHttps.Should().BeFalse();
    }

    /// <summary>
    /// A request whose client address cannot be determined must be rejected rather than dropped
    /// into a bucket every other unidentifiable caller shares.
    /// </summary>
    [Fact]
    public void RateLimiting_AnonymousFallback_Rejects()
    {
        new ApiPipelineRateLimitingOptions().AnonymousFallback.Should().Be(AnonymousFallbackBehavior.Reject);
    }

    /// <summary>
    /// The API key header is caller-supplied and unauthenticated, so partitioning on it stays off
    /// until an operator supplies an allow-list.
    /// </summary>
    [Fact]
    public void RateLimiting_ApiKeyPartitioning_IsOff()
    {
        new ApiPipelineRateLimitingOptions().EnableApiKeyPartitioning.Should().BeFalse();
    }

    /// <summary>
    /// An IPv6 end site holds a whole <c>/64</c>, so the partition key must collapse to the network
    /// or a single host gets an unbounded supply of buckets.
    /// </summary>
    [Fact]
    public void RateLimiting_IPv6Partition_CollapsesToTheAssignedNetwork()
    {
        new ApiPipelineRateLimitingOptions().IPv6PartitionPrefixLength.Should().Be(64);
    }

    /// <summary>
    /// An oversized or malformed inbound correlation id must never reach logs, traces, or
    /// downstream headers unbounded.
    /// </summary>
    [Fact]
    public void CorrelationId_InboundLength_IsBounded()
    {
        new ApiPipelineCorrelationIdOptions().MaxLength.Should().Be(128);
    }

    /// <summary>
    /// Kestrel's <c>Server</c> header is server fingerprinting with no operational value to a
    /// caller, so it is suppressed unless an operator asks for it back.
    /// </summary>
    [Fact]
    public void RequestLimits_SuppressServerHeader_IsOn()
    {
        new ApiPipelineRequestLimitsOptions().SuppressServerHeader.Should().BeTrue();
    }

    /// <summary>
    /// CORS stays closed until configured: an accidentally enabled pipeline must not open an API to
    /// browser origins nobody listed.
    /// </summary>
    [Fact]
    public void Cors_IsOff_AndDevelopmentAllowAll_IsOff()
    {
        var settings = new ApiPipelineCorsOptions();
        settings.Enabled.Should().BeFalse();
        settings.AllowAllInDevelopment.Should().BeFalse();
        settings.AllowCredentials.Should().BeFalse();
    }

    /// <summary>
    /// The forwarded-headers trust set must be enforced in Production, because an empty effective
    /// trust set makes <c>ForwardedHeadersMiddleware</c> accept <c>X-Forwarded-*</c> from anyone.
    /// </summary>
    [Fact]
    public void ForwardedHeaders_TrustSetEnforcement_IsOnInProduction()
    {
        new ApiPipelineForwardedHeadersOptions().EnforceTrustedProxyConfiguration.Should().BeTrue();
    }

    /// <summary>
    /// Request timeouts are opt-in: a ceiling nobody asked for turns a legitimately slow endpoint
    /// into an error the first time the package is upgraded.
    /// </summary>
    [Fact]
    public void RequestTimeouts_AreOptIn_WithASaneCeilingWhenEnabled()
    {
        var settings = new ApiPipelineRequestTimeoutsOptions();
        settings.Enabled.Should().BeFalse();
        settings.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(30));
        settings.TimeoutStatusCode.Should().Be(504);
    }

    /// <summary>
    /// The Kestrel connection and slow-request limits default to <c>null</c>, meaning "leave the
    /// framework's own default in place". Shipping opinionated timeouts here would change the
    /// behaviour of every consuming application on upgrade, including ones with legitimately slow
    /// uploads.
    /// </summary>
    [Fact]
    public void KestrelTimeoutLimits_DefaultToTheFrameworkDefaults()
    {
        var settings = new ApiPipelineRequestLimitsOptions();
        settings.RequestHeadersTimeout.Should().BeNull();
        settings.KeepAliveTimeout.Should().BeNull();
        settings.MaxConcurrentConnections.Should().BeNull();
        settings.MinRequestBodyDataRateBytesPerSecond.Should().BeNull();
        settings.MinRequestBodyDataRateGracePeriod.Should().BeNull();
    }

    /// <summary>
    /// The success-path rate limit headers are opt-in, so upgrading does not silently add a
    /// middleware frame and an <c>OnStarting</c> registration to every request in every application.
    /// A 429 reports the limit regardless — see <see cref="RateLimitHeaderPolicyTests"/>.
    /// </summary>
    [Fact]
    public void RateLimitInformationalHeaders_AreOptIn()
    {
        new ApiPipelineRateLimitingOptions().EmitRateLimitHeaders.Should().BeFalse();
    }

    /// <summary>
    /// HSTS is never pinned onto a loopback host by default, matching ASP.NET Core's own
    /// <c>UseHsts</c>: the pin outlives the process and cannot be cleared without editing browser
    /// state.
    /// </summary>
    [Fact]
    public void Hsts_ExcludesLoopbackHosts_ByDefault()
    {
        new ApiPipelineSecurityHeadersOptions().StrictTransportSecurityExcludeLoopbackHosts.Should().BeTrue();
    }
}
