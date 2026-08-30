using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Durations are <see cref="TimeSpan"/> settings (<c>"00:01:00"</c>). An unsupported
/// <c>...Seconds</c> key (not read) and a bare number (read as days) both fail startup, saying what to write.
/// </summary>
public sealed class DurationSettingsTests
{
    /// <summary>Every unsupported whole-number key, with the duration key to use.</summary>
    public static TheoryData<string, string> FormerKeys => new()
    {
        { "ApiPipeline:RequestTimeouts:DefaultTimeoutSeconds", "DefaultTimeout" },
        { "ApiPipeline:RequestLimits:RequestHeadersTimeoutSeconds", "RequestHeadersTimeout" },
        { "ApiPipeline:RequestLimits:KeepAliveTimeoutSeconds", "KeepAliveTimeout" },
        { "ApiPipeline:RequestLimits:MinRequestBodyDataRateGracePeriodSeconds", "MinRequestBodyDataRateGracePeriod" },
        { "ApiPipeline:SecurityHeaders:StrictTransportSecurityMaxAgeSeconds", "StrictTransportSecurityMaxAge" },
        { "ApiPipeline:EdgeRateLimiting:WindowSeconds", "Window" },
        { "ApiPipeline:RateLimiting:Policies:0:WindowSeconds", "Window" },
    };

    /// <summary>An unsupported whole-number key fails startup, naming its replacement.</summary>
    [Theory]
    [MemberData(nameof(FormerKeys))]
    public async Task FormerKey_FailsStartup_NamingItsReplacement(string key, string replacement)
    {
        var failure = await StartupFailureAsync(c => c[key] = "30");

        failure.Message.Should()
            .Contain($"'{key}' is not read")
            .And.Contain($"the setting is '{replacement}'");
    }

    /// <summary>
    /// A bare number is read as days. In range for a window, so only this check catches it: the
    /// message gives the duration for that many seconds.
    /// </summary>
    [Theory]
    [InlineData("ApiPipeline:EdgeRateLimiting:Window", "60", "\"00:01:00\" for 60 seconds")]
    [InlineData("ApiPipeline:RateLimiting:Policies:0:Window", "90", "\"00:01:30\" for 90 seconds")]
    [InlineData("ApiPipeline:RequestTimeouts:DefaultTimeout", "30", "\"00:00:30\" for 30 seconds")]
    [InlineData("ApiPipeline:Cors:PreflightMaxAge", "600", "\"00:10:00\" for 600 seconds")]
    public async Task BareNumber_FailsStartup_AsDays(string key, string value, string suggestion)
    {
        var failure = await StartupFailureAsync(c => c[key] = value);

        failure.Message.Should()
            .Contain($"'{key}' is '{value}', which reads as {value} days")
            .And.Contain(suggestion);
    }

    /// <summary>
    /// A rate-limit window is stated in seconds by <c>Retry-After</c> and <c>RateLimit-Policy</c>,
    /// so a fraction of a second would be misreported; it fails startup.
    /// </summary>
    [Theory]
    [InlineData("ApiPipeline:EdgeRateLimiting:Window")]
    [InlineData("ApiPipeline:RateLimiting:Policies:0:Window")]
    public async Task FractionalWindow_FailsStartup(string key)
    {
        var failure = await StartupFailureAsync(c => c[key] = "00:00:01.500");

        failure.Message.Should().Contain("must be whole seconds");
    }

    /// <summary>A duration outside its range fails startup with the bounds, as hh:mm:ss.</summary>
    [Fact]
    public async Task OutOfRangeDuration_FailsStartup_WithBounds()
    {
        var failure = await StartupFailureAsync(c => c["ApiPipeline:RequestLimits:KeepAliveTimeout"] = "02:00:00");

        failure.Message.Should().Contain("KeepAliveTimeout (02:00:00) must be between 00:00:01 and 01:00:00");
    }

    /// <summary>
    /// Kestrel rejects a minimum-data-rate grace period of its 1-second heartbeat or less, so startup
    /// fails here, naming the setting, instead of inside Kestrel.
    /// </summary>
    [Fact]
    public async Task GracePeriodOfOneSecond_FailsStartup()
    {
        var failure = await StartupFailureAsync(c =>
        {
            c["ApiPipeline:RequestLimits:MinRequestBodyDataRateBytesPerSecond"] = "100";
            c["ApiPipeline:RequestLimits:MinRequestBodyDataRateGracePeriod"] = "00:00:01";
        });

        failure.Message.Should().Contain("MinRequestBodyDataRateGracePeriod (00:00:01) must be more than 1 second");
    }

    /// <summary>The documented forms bind: hh:mm:ss, with fractions, and d.hh:mm:ss.</summary>
    [Fact]
    public async Task Durations_Bind_FromTimeSpanStrings()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestTimeouts:DefaultTimeout"] = "00:00:12.500";
            c["ApiPipeline:SecurityHeaders:StrictTransportSecurityMaxAge"] = "730.00:00:00";
            c["ApiPipeline:EdgeRateLimiting:Window"] = "00:05:00";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);

        app.Services.GetRequiredService<IOptions<ApiPipelineRequestTimeoutsOptions>>().Value.DefaultTimeout
            .Should().Be(TimeSpan.FromMilliseconds(12_500));
        app.Services.GetRequiredService<IOptions<ApiPipelineSecurityHeadersOptions>>().Value.StrictTransportSecurityMaxAge
            .Should().Be(TimeSpan.FromDays(730));
        app.Services.GetRequiredService<IOptions<ApiPipelineEdgeRateLimitingOptions>>().Value.Window
            .Should().Be(TimeSpan.FromMinutes(5));
    }

    private static async Task<OptionsValidationException> StartupFailureAsync(Action<Dictionary<string, string?>> configure)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            // A valid policy, so a policy key under test is the only problem with it. Enabled, or
            // the policies are not validated.
            c["ApiPipeline:RateLimiting:Enabled"] = "true";
            c["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict";
            c["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "10";
            c["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00";
            configure(c);
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var start = () => app.StartAsync(TestContext.Current.CancellationToken);

        return (await start.Should().ThrowAsync<OptionsValidationException>()).Which;
    }
}
