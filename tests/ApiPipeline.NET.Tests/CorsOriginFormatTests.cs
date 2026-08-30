using ApiPipeline.NET.Cors;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// A browser's <c>Origin</c> header is exactly <c>scheme://host[:port]</c>: no path, no trailing
/// slash, no default port. The framework compares allowed origins as strings, so an entry such as
/// <c>https://app.example.com/</c> never matches and CORS fails silently. Such entries are rejected
/// at startup, with the corrected value in the message.
/// </summary>
public sealed class CorsOriginFormatTests
{
    private static ValidateOptionsResult Validate(params string[] origins) =>
        new CorsAllowedOriginsValidator().Validate(
            Options.DefaultName,
            new ApiPipelineCorsOptions { Enabled = true, AllowedOrigins = origins });

    /// <summary>Exactly the forms a browser sends, plus <c>*</c>, IPv6, IDN and app schemes.</summary>
    [Theory]
    [InlineData("*")]
    [InlineData("https://app.example.com")]
    [InlineData("https://Portal.Example.com")]
    [InlineData("http://localhost:3000")]
    [InlineData("https://app.example.com:8443")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("https://xn--bcher-kva.example")]
    [InlineData("https://bücher.example")]
    [InlineData("capacitor://localhost")]
    public void WellFormedOrigin_IsAccepted(string origin) =>
        Validate(origin).Succeeded.Should().BeTrue();

    /// <summary>Parseable but never matching: the message gives the value that would match.</summary>
    [Theory]
    [InlineData("https://app.example.com/", "https://app.example.com")]
    [InlineData("https://app.example.com/app", "https://app.example.com")]
    [InlineData("https://app.example.com?x=1", "https://app.example.com")]
    [InlineData("https://app.example.com#top", "https://app.example.com")]
    [InlineData("https://app.example.com:443", "https://app.example.com")]
    [InlineData("http://localhost:80/", "http://localhost")]
    [InlineData(" https://app.example.com", "https://app.example.com")]
    public void OriginThatCannotMatch_IsRejected_WithTheCorrectedValue(string origin, string corrected)
    {
        var result = Validate(origin);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ApiPipeline:Cors:AllowedOrigins")
            .And.Contain($"'{origin}'")
            .And.Contain($"'{corrected}'");
    }

    /// <summary>Not an origin at all, a wildcard pattern the package does not support, or the unsafe <c>null</c> origin.</summary>
    [Theory]
    [InlineData("app.example.com")]
    [InlineData("https://*.example.com")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("https://user@app.example.com")]
    [InlineData("file:///index.html")]
    public void MalformedOrigin_IsRejected(string origin)
    {
        var result = Validate(origin);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ApiPipeline:Cors:AllowedOrigins").And.Contain($"'{origin}'");
    }

    /// <summary>Every bad entry is reported at once, not one per deploy.</summary>
    [Fact]
    public void EveryBadOrigin_IsReported()
    {
        var result = Validate("https://ok.example.com", "https://a.example.com/", "https://*.example.com");

        result.FailureMessage.Should().Contain("'https://a.example.com/'").And.Contain("'https://*.example.com'")
            .And.NotContain("'https://ok.example.com'");
    }

    /// <summary>As with the other CORS rules, a disabled feature is not checked.</summary>
    [Fact]
    public void Disabled_IsNotChecked() =>
        new CorsAllowedOriginsValidator().Validate(
                Options.DefaultName,
                new ApiPipelineCorsOptions { Enabled = false, AllowedOrigins = ["https://app.example.com/"] })
            .Succeeded.Should().BeTrue();

    private static ValidateOptionsResult ValidateCredentialed(string environment, bool allowCredentials, params string[] origins) =>
        new CorsAllowedOriginsValidator(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment })
            .Validate(
                Options.DefaultName,
                new ApiPipelineCorsOptions { Enabled = true, AllowCredentials = allowCredentials, AllowedOrigins = origins });

    /// <summary>
    /// A plain-HTTP origin's pages can be rewritten by anyone on the network path, so allowing it
    /// with credentials hands that attacker the user's session on this API. Outside Development
    /// startup fails, naming the entry.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("prd")]
    public void HttpOrigin_WithCredentials_OutsideDevelopment_IsRejected(string environment)
    {
        var result = ValidateCredentialed(environment, allowCredentials: true, "https://app.example.com", "http://legacy.example.com");

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("'http://legacy.example.com'").And.Contain("AllowCredentials")
            .And.NotContain("'https://app.example.com'");
    }

    /// <summary>A local front end on <c>http://localhost</c> is the normal Development setup.</summary>
    [Fact]
    public void HttpOrigin_WithCredentials_InDevelopment_IsAccepted() =>
        ValidateCredentialed(Environments.Development, allowCredentials: true, "http://localhost:3000")
            .Succeeded.Should().BeTrue();

    /// <summary>Without credentials a cross-origin call carries no session, so the scheme is the operator's call.</summary>
    [Fact]
    public void HttpOrigin_WithoutCredentials_IsAccepted() =>
        ValidateCredentialed(Environments.Production, allowCredentials: false, "http://legacy.example.com")
            .Succeeded.Should().BeTrue();

    /// <summary>
    /// The framework treats <c>*</c> as "any origin" only when it is the sole entry; listed with
    /// other origins it is compared as a literal and matches nothing, so the operator who meant
    /// "everyone" gets only the listed origins, with no error. Startup fails instead.
    /// </summary>
    [Fact]
    public void Wildcard_WithOtherOrigins_IsRejected()
    {
        var result = Validate("https://app.example.com", "*");

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("'*'").And.Contain("only entry");
    }

    /// <summary>A trailing-slash origin fails startup, naming the entry and its fix.</summary>
    [Fact]
    public async Task TrailingSlashOrigin_FailsStartup()
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = "true";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://app.example.com/";
        });

        await using var app = await TestAppBuilder.CreateAppAsync(config);
        app.MapGet("/test", () => "ok");

        var act = () => app.StartAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*'https://app.example.com/'*'https://app.example.com'*");
    }
}
