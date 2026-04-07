using System.Net;
using ApiPipeline.NET.Cors;
using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.Options;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Tests for the CORS (Cross-Origin Resource Sharing) pipeline feature, covering
/// disabled mode, development allow-all mode, production explicit-origin mode,
/// and OPTIONS preflight handling.
/// </summary>
public sealed class CorsTests
{
    /// <summary>
    /// Verifies that no CORS headers are added to responses when the CORS feature is disabled,
    /// regardless of whether the request carries an <c>Origin</c> header.
    /// </summary>
    [Fact]
    public async Task Cors_Disabled_Does_Not_Add_Headers()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://example.com");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    /// <summary>
    /// Verifies that enabling CORS with <c>AllowAllInDevelopment</c> set to <c>true</c>
    /// causes any origin to be reflected back in the <c>Access-Control-Allow-Origin</c>
    /// response header when the environment is Development.
    /// </summary>
    [Fact]
    public async Task Cors_AllowAll_In_Development_Allows_Any_Origin()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowAllInDevelopment"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Development);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://any-origin.test");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
    }

    /// <summary>
    /// Verifies that in Production, only explicitly listed origins receive the
    /// <c>Access-Control-Allow-Origin</c> header, while unlisted origins do not.
    /// </summary>
    [Fact]
    public async Task Cors_Production_With_Explicit_Origins()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowAllInDevelopment"] = "false";
            c["CorsOptions:AllowedOrigins:0"] = "https://allowed.example.com";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Production);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();

        // Allowed origin
        var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/test");
        allowedRequest.Headers.Add("Origin", "https://allowed.example.com");
        var allowedResponse = await client.SendAsync(allowedRequest);
        allowedResponse.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();

        // Disallowed origin
        var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/test");
        deniedRequest.Headers.Add("Origin", "https://evil.example.com");
        var deniedResponse = await client.SendAsync(deniedRequest);
        deniedResponse.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    /// <summary>
    /// Verifies that the default value of <see cref="ApiPipeline.NET.Options.CorsSettings.AllowedHeaders"/>
    /// is a safe explicit list and does not include a wildcard.
    /// </summary>
    [Fact]
    public void CorsSettings_Default_AllowedHeaders_Does_Not_Include_Wildcard()
    {
        var settings = new ApiPipeline.NET.Options.CorsSettings();

        settings.AllowedHeaders.Should().NotContain("*");
        settings.AllowedHeaders.Should().Contain("Content-Type");
        settings.AllowedHeaders.Should().Contain("Authorization");
        settings.AllowedHeaders.Should().Contain("X-Correlation-Id");
    }

    /// <summary>
    /// Verifies that an OPTIONS preflight request receives <c>Access-Control-Allow-Origin</c>
    /// and <c>Access-Control-Allow-Methods</c> headers, confirming that the CORS policy
    /// handles preflight negotiation correctly.
    /// </summary>
    [Fact]
    public async Task Cors_Preflight_Returns_Correct_Methods()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowAllInDevelopment"] = "true";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config, Environments.Development);
        app.UseRouting();
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/test");
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        response.Headers.Contains("Access-Control-Allow-Methods").Should().BeTrue();
    }

    /// <summary>
    /// Verifies that AllowAllInDevelopment defaults to false to prevent accidental
    /// wildcard CORS in staging/CI environments where ASPNETCORE_ENVIRONMENT=Development.
    /// </summary>
    [Fact]
    public void CorsSettings_AllowAllInDevelopment_DefaultIs_False()
    {
        var settings = new ApiPipeline.NET.Options.CorsSettings();
        settings.AllowAllInDevelopment.Should().BeFalse(
            "wildcard CORS must be explicit opt-in to avoid accidental exposure in staging");
    }

    /// <summary>
    /// Verifies that an origin explicitly listed in AllowedOrigins is accepted.
    /// </summary>
    [Fact]
    public async Task Cors_Allows_Configured_Origin()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowedOrigins:0"] = "https://trusted.example.com";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://trusted.example.com");

        var response = await client.SendAsync(request);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
    }

    /// <summary>
    /// Verifies that an origin NOT in AllowedOrigins is rejected (no ACAO header).
    /// </summary>
    [Fact]
    public async Task Cors_Rejects_Unknown_Origin()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["CorsOptions:Enabled"] = "true";
            c["CorsOptions:AllowedOrigins:0"] = "https://trusted.example.com";
        });
        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.UseCors();
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync();

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://evil.attacker.com");

        var response = await client.SendAsync(request);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    /// <summary>
    /// Verifies that CORS policy reflects runtime configuration changes through IOptionsMonitor.
    /// </summary>
    [Fact]
    public async Task Cors_Reload_Updates_AllowedOrigins_Without_Restart()
    {
        var monitor = new TestOptionsMonitor(new CorsSettings
        {
            Enabled = true,
            AllowedOrigins = ["https://old.example.com"],
            AllowedMethods = ["GET"],
            AllowedHeaders = ["Content-Type"]
        });

        var provider = new LiveConfigCorsPolicyProvider(monitor, new TestHostEnvironment(Environments.Production));
        var firstPolicy = await provider.GetPolicyAsync(new DefaultHttpContext(), null);
        firstPolicy!.Origins.Should().Contain("https://old.example.com");

        monitor.Update(new CorsSettings
        {
            Enabled = true,
            AllowedOrigins = ["https://new.example.com"],
            AllowedMethods = ["GET"],
            AllowedHeaders = ["Content-Type"]
        });

        var secondPolicy = await provider.GetPolicyAsync(new DefaultHttpContext(), null);
        secondPolicy!.Origins.Should().NotContain("https://old.example.com");
        secondPolicy.Origins.Should().Contain("https://new.example.com");
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestOptionsMonitor(CorsSettings initial) : IOptionsMonitor<CorsSettings>
    {
        private CorsSettings _current = initial;
        private event Action<CorsSettings, string?>? Changed;

        public CorsSettings CurrentValue => _current;

        public CorsSettings Get(string? name) => _current;

        public IDisposable OnChange(Action<CorsSettings, string?> listener)
        {
            Changed += listener;
            return new ChangeToken(listener, this);
        }

        public void Update(CorsSettings next)
        {
            _current = next;
            Changed?.Invoke(next, null);
        }

        private sealed class ChangeToken(Action<CorsSettings, string?> listener, TestOptionsMonitor monitor) : IDisposable
        {
            public void Dispose() => monitor.Changed -= listener;
        }
    }
}
