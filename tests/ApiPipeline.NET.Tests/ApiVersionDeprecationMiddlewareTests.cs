using System.Net;
using ApiPipeline.NET.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for ApiVersionDeprecationOptions validation and the SunsetLink header-injection guard.
/// </summary>
public sealed class ApiVersionDeprecationMiddlewareTests
{
    /// <summary>
    /// Verifies that a valid absolute URL passes [Url] annotation validation on SunsetLink.
    /// </summary>
    [Fact]
    public async Task SunsetLink_ValidAbsoluteUrl_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiVersionDeprecationOptions:Enabled"] = "true";
            c["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0";
            c["ApiVersionDeprecationOptions:DeprecatedVersions:0:SunsetLink"] = "https://docs.example.com/v1-sunset";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync("a valid absolute URL should pass [Url] annotation validation");
    }

    /// <summary>
    /// Verifies that a relative path is rejected by the [Url] annotation on SunsetLink.
    /// Relative paths cannot be written safely to the Link header and indicate misconfiguration.
    /// </summary>
    [Fact]
    public async Task SunsetLink_RelativePath_Fails_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiVersionDeprecationOptions:Enabled"] = "true";
            c["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0";
            c["ApiVersionDeprecationOptions:DeprecatedVersions:0:SunsetLink"] = "/relative/path";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*SunsetLink*");
    }

    /// <summary>
    /// Verifies that a null SunsetLink is valid (field is optional).
    /// </summary>
    [Fact]
    public async Task SunsetLink_Null_Passes_Validation()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiVersionDeprecationOptions:Enabled"] = "true";
            c["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that when UseApiVersionDeprecation middleware is registered, a request
    /// that does not match any deprecated version receives no deprecation headers.
    /// </summary>
    [Fact]
    public async Task No_Deprecation_Headers_For_Non_Deprecated_Path()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiVersionDeprecationOptions:Enabled"] = "true";
            c["ApiVersionDeprecationOptions:PathPrefix"] = "/api";
            c["ApiVersionDeprecationOptions:DeprecatedVersions:0:Version"] = "1.0";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseApiVersionDeprecation();
        app.MapGet("/health", () => Results.Ok("ok")); // doesn't start with /api
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Deprecation").Should().BeFalse();
    }
}
