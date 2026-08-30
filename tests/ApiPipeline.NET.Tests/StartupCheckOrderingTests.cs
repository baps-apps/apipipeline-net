using ApiPipeline.NET.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The startup checks finish before the server starts listening, however the host is built and
/// whether or not it starts its hosted services concurrently. A check that fails start must leave the
/// server unstarted, so no request is ever served by a host that failed its checks.
/// </summary>
public sealed class StartupCheckOrderingTests
{
    /// <summary>Minimal API host (<c>WebApplication</c>), hosted services started one by one or concurrently.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebApplication_FailingCheck_ServerNeverStarts(bool concurrent)
    {
        var server = new RecordingServer();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.Configure<HostOptions>(o => o.ServicesStartConcurrently = concurrent);
        builder.Services.AddSingleton<IServer>(server);
        builder.Services.AddApiPipelineStartupCheck(_ => new FailingCheck());
        await using var app = builder.Build();

        var start = () => app.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage(FailingCheck.Message);
        server.Started.Should().BeFalse();
    }

    /// <summary>
    /// Generic host with <c>ConfigureWebHost</c>, the pipeline registered after it: the web host's
    /// hosted service, which starts the server, comes before the checks in registration order.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericHost_PipelineRegisteredAfterWebHost_FailingCheck_ServerNeverStarts(bool concurrent)
    {
        var server = new RecordingServer();
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureWebHost(web => web
                .UseServer(server)
                .Configure(app => app.Run(_ => Task.CompletedTask)))
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ServicesStartConcurrently = concurrent);
                services.AddApiPipelineStartupCheck(_ => new FailingCheck());
            })
            .Build();

        var start = () => host.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage(FailingCheck.Message);
        server.Started.Should().BeFalse();
    }

    /// <summary>A check that passes has run by the time the server starts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericHost_PassingCheck_RunsBeforeServerStarts(bool concurrent)
    {
        var check = new RecordingCheck();
        var server = new RecordingServer(check);
        using var host = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web => web
                .UseServer(server)
                .Configure(app => app.Run(_ => Task.CompletedTask)))
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ServicesStartConcurrently = concurrent);
                services.AddApiPipelineStartupCheck(_ => check);
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        server.Started.Should().BeTrue();
        server.CheckHadRunWhenStarted.Should().BeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FailingCheck : IApiPipelineStartupCheck
    {
        public const string Message = "startup check failed";

        public void Run() => throw new InvalidOperationException(Message);
    }

    private sealed class RecordingCheck : IApiPipelineStartupCheck
    {
        public bool HasRun { get; private set; }

        public void Run() => HasRun = true;
    }

    private sealed class RecordingServer(RecordingCheck? check = null) : IServer
    {
        public bool Started { get; private set; }

        public bool CheckHadRunWhenStarted { get; private set; }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
        {
            Started = true;
            CheckHadRunWhenStarted = check?.HasRun ?? false;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
