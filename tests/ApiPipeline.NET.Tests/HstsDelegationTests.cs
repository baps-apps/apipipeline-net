using FluentAssertions;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// <c>Strict-Transport-Security</c> is written by ASP.NET Core's own <c>HstsMiddleware</c>
/// (<c>UseHsts</c>), configured from <c>ApiPipeline:SecurityHeaders</c>, rather than by a copy of it.
/// </summary>
public sealed class HstsDelegationTests
{
    /// <summary>Fails if the HSTS settings are not translated into the framework's <see cref="HstsOptions"/>.</summary>
    [Fact]
    public async Task SecurityHeadersOptions_ConfigureTheFrameworkHstsOptions()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders().Also(c =>
        {
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityMaxAge"] = "00:10:00";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityIncludeSubDomains"] = "false";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityExcludedHosts:0"] = "ingress.internal";
        }));

        var hsts = app.Services.GetRequiredService<IOptions<HstsOptions>>().Value;

        hsts.MaxAge.Should().Be(TimeSpan.FromSeconds(600));
        hsts.IncludeSubDomains.Should().BeFalse();
        hsts.Preload.Should().BeFalse();
        hsts.ExcludedHosts.Should().Contain(["ingress.internal", "localhost"]);
    }

    /// <summary>A preload that meets the list's requirements reaches the framework's options.</summary>
    [Fact]
    public async Task Preload_WithSubdomainsAndOneYear_ConfiguresTheFrameworkHstsOptions()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders().Also(c =>
        {
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityMaxAge"] = "365.00:00:00";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityIncludeSubDomains"] = "true";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityPreload"] = "true";
        }));

        await app.StartAsync(TestContext.Current.CancellationToken);

        app.Services.GetRequiredService<IOptions<HstsOptions>>().Value.Preload.Should().BeTrue();
    }

    /// <summary>
    /// The preload list (hstspreload.org) refuses a header without <c>includeSubDomains</c> or with a
    /// <c>max-age</c> under one year, so <c>preload</c> on such a header is a submission that will be
    /// rejected, or a domain already listed that later breaks the requirements and is removed. Startup
    /// fails instead.
    /// </summary>
    [Theory]
    [InlineData("false", "365.00:00:00", "StrictTransportSecurityIncludeSubDomains")]
    [InlineData("true", "180.00:00:00", "31536000")]
    [InlineData("true", "364.23:59:59", "31536000")]
    public async Task Preload_NotMeetingTheListRequirements_FailsStartup(string includeSubDomains, string maxAge, string expected)
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders().Also(c =>
        {
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityMaxAge"] = maxAge;
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityIncludeSubDomains"] = includeSubDomains;
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityPreload"] = "true";
        }));

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .WithMessage($"*StrictTransportSecurityPreload*{expected}*");
    }

    /// <summary>With HSTS itself off, the preload flag sends nothing, so it is not checked.</summary>
    [Fact]
    public async Task Preload_WithHstsOff_IsNotChecked()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders().Also(c =>
        {
            c["ApiPipeline:SecurityHeaders:EnableStrictTransportSecurity"] = "false";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityIncludeSubDomains"] = "false";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityPreload"] = "true";
        }));

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    /// <summary>Turning the loopback exclusion off clears the framework's default exclusions.</summary>
    [Fact]
    public async Task LoopbackExclusionOff_ClearsTheFrameworkDefaultExclusions()
    {
        await using var app = await TestAppBuilder.CreateAppAsync(TestAppBuilder.WithSecurityHeaders().Also(c =>
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityExcludeLoopbackHosts"] = "false"));

        var hsts = app.Services.GetRequiredService<IOptions<HstsOptions>>().Value;

        hsts.ExcludedHosts.Should().BeEmpty();
    }
}
