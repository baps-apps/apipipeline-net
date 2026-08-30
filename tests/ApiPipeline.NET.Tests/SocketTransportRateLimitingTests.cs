using System.Net;
using System.Net.Sockets;
using ApiPipeline.NET.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// On a Unix socket every connection has a null <c>RemoteIpAddress</c>, so with
/// <c>AnonymousFallback = Reject</c> every anonymous request is rejected. Startup says so.
/// </summary>
public sealed class SocketTransportRateLimitingTests
{
    private const string WarningMarker = "no client IP address";

    /// <summary>The hazard itself: an anonymous request over the socket gets a 429, even with X-Forwarded-For.</summary>
    [Fact]
    public async Task UnixSocket_WithRejectFallback_RejectsAnonymousRequests_AndWarnsAtStartup()
    {
        var socketPath = SocketPath();
        var capture = new CapturingLoggerProvider();

        await using var app = Build(
            o => o.ListenUnixSocket(socketPath),
            capture,
            anonymousFallback: "Reject",
            rateLimitingEnabled: true);
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            using var client = UnixSocketClient(socketPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            capture.Messages.Should().ContainSingle(m => m.Contains(WarningMarker, StringComparison.Ordinal))
                .Which.Should().Contain(socketPath)
                .And.Contain("ApiPipeline:RateLimiting:AnonymousFallback");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            File.Delete(socketPath);
        }
    }

    /// <summary>Nothing to report when the hazard cannot occur.</summary>
    [Theory]
    [InlineData("Allow", true)]
    [InlineData("RateLimit", true)]
    [InlineData("Reject", false)]
    public async Task UnixSocket_WithoutTheHazard_DoesNotWarn(string anonymousFallback, bool rateLimitingEnabled)
    {
        var socketPath = SocketPath();
        var capture = new CapturingLoggerProvider();

        await using var app = Build(o => o.ListenUnixSocket(socketPath), capture, anonymousFallback, rateLimitingEnabled);
        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);
        File.Delete(socketPath);

        capture.Messages.Should().NotContain(m => m.Contains(WarningMarker, StringComparison.Ordinal));
    }

    /// <summary>A TCP listener carries the peer address, so the default fallback is safe there.</summary>
    [Fact]
    public async Task TcpLoopback_WithRejectFallback_DoesNotWarn()
    {
        var capture = new CapturingLoggerProvider();

        await using var app = Build(o => o.Listen(IPAddress.Loopback, 0), capture, "Reject", rateLimitingEnabled: true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);

        capture.Messages.Should().NotContain(m => m.Contains(WarningMarker, StringComparison.Ordinal));
    }

    /// <summary>Kestrel reports Unix sockets as <c>unix:</c> and named pipes as <c>pipe:</c> addresses.</summary>
    [Theory]
    [InlineData("http://unix:/tmp/app.sock", true)]
    [InlineData("https://unix:/var/run/app.sock", true)]
    [InlineData("HTTP://UNIX:/tmp/app.sock", true)]
    [InlineData("http://pipe:/app-pipe", true)]
    [InlineData("http://127.0.0.1:8080", false)]
    [InlineData("http://[::]:8080", false)]
    [InlineData("http://unixhost:8080", false)]
    public void HasNoPeerAddress_RecognisesSocketAndPipeListeners(string address, bool expected) =>
        SocketTransportRateLimitingWarning.HasNoPeerAddress(address).Should().Be(expected);

    private static WebApplication Build(
        Action<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions> listen,
        CapturingLoggerProvider capture,
        string anonymousFallback,
        bool rateLimitingEnabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.ConfigureKestrel(listen);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiPipeline:RateLimiting:Enabled"] = rateLimitingEnabled ? "true" : "false",
            ["ApiPipeline:RateLimiting:AnonymousFallback"] = anonymousFallback,
            ["ApiPipeline:RateLimiting:DefaultPolicy"] = "default",
            ["ApiPipeline:RateLimiting:Policies:0:Name"] = "default",
            ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "100",
            ["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00",
            // The sidecar proxy is trusted, as a Production setup must configure.
            ["ApiPipeline:ForwardedHeaders:Enabled"] = "true",
            ["ApiPipeline:ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.Services.AddApiPipeline(builder.Configuration);

        var app = builder.Build();
        app.UseApiPipeline(p => p.WithForwardedHeaders().WithRateLimiting());
        app.MapGet("/test", () => "ok");
        return app;
    }

    // Short: Unix socket paths are limited to about 104 bytes on macOS.
    private static string SocketPath() =>
        Path.Combine(Path.GetTempPath(), $"ap-{Guid.NewGuid().ToString("N")[..8]}.sock");

    private static HttpClient UnixSocketClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }
}
