using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Makes requests look like HTTPS. TestServer always uses plain HTTP, so HSTS tests need this.
/// </summary>
internal static class TestHttpsScheme
{
    /// <summary>Marks every request as HTTPS. Register before the middleware under test.</summary>
    public static WebApplication UseSimulatedHttps(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Request.Scheme = "https";
            await next(context);
        });
        return app;
    }
}

/// <summary>
/// A test client with a non-loopback host. TestServer defaults to <c>localhost</c>, which never
/// receives HSTS.
/// </summary>
internal static class ApiTestClient
{
    /// <summary>The host used by tests that need to look like a real caller.</summary>
    public const string Host = "api.example.test";

    /// <summary>A test client addressed to <see cref="Host"/> over plain HTTP.</summary>
    public static HttpClient GetApiTestClient(this WebApplication app)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri($"http://{Host}/");
        return client;
    }
}

/// <summary>
/// Gives a TestServer request a peer address. Without one, the rate limiter takes its
/// <c>AnonymousFallback</c> path instead of IP partitioning.
/// </summary>
internal static class TestPeerAddress
{
    /// <summary>Request header carrying the address to present as the peer.</summary>
    public const string HeaderName = "X-Test-Peer-Address";

    /// <summary>Applies the address from <see cref="HeaderName"/>. Register before the pipeline.</summary>
    public static WebApplication UseSimulatedPeerAddress(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value)
                && System.Net.IPAddress.TryParse(value.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await next(context);
        });
        return app;
    }
}
