using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// HSTS must not be pinned onto loopback hosts. ASP.NET Core's own <c>UseHsts</c> excludes
/// <c>localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c> for this reason: a browser that receives HSTS
/// for <c>localhost</c> refuses plain HTTP to <em>every</em> local development server afterwards,
/// and the pin cannot be cleared without editing browser state.
/// </summary>
public sealed class SecurityHeadersExcludedHostTests
{
    private static Dictionary<string, string?> Config(Action<Dictionary<string, string?>>? extra = null) =>
        TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
            extra?.Invoke(c);
        });

    private static async Task<HttpResponseMessage> GetWithHostAsync(
        Dictionary<string, string?> config,
        string host)
    {
        var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.UseSimulatedHttps();
        app.UseApiPipeline(pipeline => pipeline.WithSecurityHeaders());
        app.MapGet("/ok", () => Results.Ok("ok"));
        await app.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/ok");
        var response = await app.GetTestClient().SendAsync(request);
        await app.DisposeAsync();
        return response;
    }

    /// <summary>Loopback by name is excluded, matching <c>UseHsts</c>.</summary>
    [Fact]
    public async Task Hsts_IsNotSent_ToLocalhost()
    {
        var response = await GetWithHostAsync(Config(), "localhost");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    /// <summary>Loopback by IPv4 literal is excluded too.</summary>
    [Fact]
    public async Task Hsts_IsNotSent_ToLoopbackAddress()
    {
        var response = await GetWithHostAsync(Config(), "127.0.0.1");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    /// <summary>A real host still gets HSTS — the exclusion must not disable the header.</summary>
    [Fact]
    public async Task Hsts_IsSent_ToRealHost()
    {
        var response = await GetWithHostAsync(Config(), "api.example.com");

        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue();
    }

    /// <summary>
    /// The exclusion list is configurable: a deployment whose ingress presents an internal host
    /// name it does not want pinned needs to add it, and one that genuinely serves HSTS on
    /// localhost needs to clear the list.
    /// </summary>
    [Fact]
    public async Task Hsts_ExcludedHosts_AreConfigurable()
    {
        var response = await GetWithHostAsync(
            Config(c => c["ApiPipeline:SecurityHeaders:StrictTransportSecurityExcludedHosts:0"] = "internal.example.com"),
            "internal.example.com");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    /// <summary>
    /// Opting back in is a single flag, for a deployment that genuinely serves HSTS on loopback.
    /// </summary>
    [Fact]
    public async Task Hsts_LoopbackExclusionDisabled_SendsToLocalhost()
    {
        var response = await GetWithHostAsync(
            Config(c => c["ApiPipeline:SecurityHeaders:StrictTransportSecurityExcludeLoopbackHosts"] = "false"),
            "localhost");

        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue();
    }

    /// <summary>
    /// The exclusion defaults are expressed as a flag plus an empty list, not as a pre-populated
    /// list: the configuration binder appends to an existing collection, so a defaulted list could
    /// never be cleared by an operator — only added to.
    /// </summary>
    [Fact]
    public void Hsts_ExclusionDefaults_AreClearableFromConfiguration()
    {
        var defaults = new ApiPipelineSecurityHeadersOptions();

        defaults.StrictTransportSecurityExcludeLoopbackHosts.Should().BeTrue(
            "matches the loopback set ASP.NET Core's UseHsts excludes");
        defaults.StrictTransportSecurityExcludedHosts.Should().BeNullOrEmpty(
            "a defaulted array cannot be cleared through configuration, only appended to");
    }
}
