using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for request limits configuration, validation, and runtime enforcement.
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
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "10485760";
            c["ApiPipeline:RequestLimits:MaxRequestHeadersTotalSize"] = "32768";
            c["ApiPipeline:RequestLimits:MaxRequestHeaderCount"] = "100";
            c["ApiPipeline:RequestLimits:MaxFormValueCount"] = "1024";
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
        var config = TestAppBuilder.MinimalConfig();
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
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxFormValueCount"] = "0";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*MaxFormValueCount*");
    }

    /// <summary>
    /// Verifies that ConfigureKestrelOptions applies MaxRequestBodySize to KestrelServerOptions.
    /// TestServer bypasses Kestrel's transport-level limits, so we verify the option is wired
    /// by resolving the configured IConfigureOptions and checking the resulting KestrelServerOptions.
    /// </summary>
    [Fact]
    public async Task RequestLimits_ConfiguresKestrelOptions_MaxRequestBodySize()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "1024";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var kestrelConfigurators = app.Services
            .GetServices<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>();

        var kestrelOptions = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions();
        foreach (var configurator in kestrelConfigurators)
        {
            configurator.Configure(kestrelOptions);
        }

        kestrelOptions.Limits.MaxRequestBodySize.Should().Be(1024);
    }

    /// <summary>
    /// Verifies that SuppressServerHeader = true sets AddServerHeader = false on KestrelServerOptions.
    /// </summary>
    [Fact]
    public async Task SuppressServerHeader_Disables_Kestrel_ServerHeader()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "false";
            c["ApiPipeline:RequestLimits:SuppressServerHeader"] = "true";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var kestrelConfigurators = app.Services
            .GetServices<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>();

        var kestrelOptions = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions();
        foreach (var configurator in kestrelConfigurators)
        {
            configurator.Configure(kestrelOptions);
        }

        kestrelOptions.AddServerHeader.Should().BeFalse(
            "SuppressServerHeader = true should disable the Kestrel Server header");
    }

    /// <summary>
    /// Verifies that posting a form with more values than MaxFormValueCount is rejected at runtime.
    /// This proves limits are enforced, not just validated.
    /// </summary>
    [Fact]
    public async Task OversizedForm_Is_Rejected_At_Runtime()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxFormValueCount"] = "2";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);
        app.MapPost("/form", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            return Results.Ok(new { Count = form.Count });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var formData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("field1", "value1"),
            new KeyValuePair<string, string>("field2", "value2"),
            new KeyValuePair<string, string>("field3", "value3"),
        });

        var response = await client.PostAsync("/form", formData, TestContext.Current.CancellationToken);
        response.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "form with 3 values should be rejected when MaxFormValueCount = 2");
    }

    /// <summary>
    /// Verifies that a request body within limits is accepted.
    /// </summary>
    [Fact]
    public async Task RequestWithinLimits_Is_Accepted()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:RequestLimits:Enabled"] = "true";
            c["ApiPipeline:RequestLimits:MaxRequestBodySize"] = "10485760";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapPost("/upload", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Ok(new { Length = body.Length });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        var payload = new string('x', 100);
        var content = new StringContent(payload, Encoding.UTF8, "text/plain");

        var response = await client.PostAsync("/upload", content, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
