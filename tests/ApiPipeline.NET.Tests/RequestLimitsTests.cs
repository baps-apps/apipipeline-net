using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for request limits configuration and validation.
/// </summary>
public sealed class RequestLimitsTests
{
    /// <summary>
    /// Verifies that all nullable limit properties accept valid positive values.
    /// </summary>
    [Fact]
    public async Task RequestLimits_ValidPositiveValues_Pass_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RequestLimitsOptions:Enabled"] = "true";
            c["RequestLimitsOptions:MaxRequestBodySize"] = "10485760";
            c["RequestLimitsOptions:MaxRequestHeadersTotalSize"] = "32768";
            c["RequestLimitsOptions:MaxRequestHeaderCount"] = "100";
            c["RequestLimitsOptions:MaxFormValueCount"] = "1024";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that limits disabled still passes validation with no values set.
    /// </summary>
    [Fact]
    public async Task RequestLimits_Disabled_No_Values_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(); // RequestLimitsOptions:Enabled defaults to false
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies MaxFormValueCount = 0 fails validation when limits are enabled.
    /// </summary>
    [Fact]
    public async Task RequestLimits_MaxFormValueCount_Zero_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["RequestLimitsOptions:Enabled"] = "true";
            c["RequestLimitsOptions:MaxFormValueCount"] = "0";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*MaxFormValueCount*");
    }
}
