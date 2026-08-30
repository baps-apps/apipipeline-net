using System.Net;
using ApiPipeline.NET.Pipeline;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Cross-cutting contracts of the assembled pipeline: what a caller sees on responses the pipeline
/// short-circuits, and what an integrating application is told when the pipeline takes something
/// over.
/// </summary>
public sealed class PipelineContractTests
{
    /// <summary>
    /// Captures log records so a warning can be asserted on rather than eyeballed.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Messages);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add(formatter(state, exception));
                }
            }
        }
    }

    /// <summary>
    /// A validation rejection must carry the same RFC 9457 body shape as every other error, so
    /// clients have one error contract to parse. Any middleware or endpoint filter that
    /// short-circuits with a bare status code gets the same treatment, which is why none of them
    /// need to write a body themselves.
    /// </summary>
    [Fact]
    public async Task ShortCircuitedStatusCode_IsTurnedIntoProblemJson()
    {
        var config = TestAppBuilder.MinimalConfig();
        await using var app = await TestAppBuilder.CreateAppAsync(config, addProblemDetails: true);

        app.UseConsumerErrorHandling();
        app.UseApiPipeline(pipeline => pipeline
            .WithCorrelationId());

        // Stands in for anything downstream that rejects a request without writing a body: an
        // endpoint filter returning Results.Problem, an authorization handler, application
        // middleware. UseStatusCodePages is what gives all of them one error shape.
        app.Use((HttpContext context, RequestDelegate _) =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });

        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        response.Headers.CacheControl?.NoStore.Should().BeTrue(
            "a rejection must never be cached by an intermediary");
    }

    /// <summary>
    /// An application <c>AddDefaultPolicy</c> would be overridden by the pipeline's provider; in
    /// Production that fails startup rather than a warning that scrolls past.
    /// </summary>
    [Fact]
    public async Task Cors_ConsumerAddDefaultPolicy_InProduction_FailsStartup()
    {
        await using var app = BuildAppWithConflictingCorsDefault(
            Environments.Production, new CapturingLoggerProvider());

        var act = async () => await app.StartAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*will NOT be applied*");
    }

    /// <summary>
    /// The guard keys on Development, not Production, so an environment named <c>Prod</c> or
    /// <c>Staging</c> fails closed too.
    /// </summary>
    [Theory]
    [InlineData("Prod")]
    [InlineData("Staging")]
    public async Task Cors_ConsumerAddDefaultPolicy_InAnyNonDevelopmentEnvironment_FailsStartup(string environment)
    {
        await using var app = BuildAppWithConflictingCorsDefault(environment, new CapturingLoggerProvider());

        var act = async () => await app.StartAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*will NOT be applied*'{environment}'*");
    }

    /// <summary>
    /// In Development the same conflict stays a warning: a developer sees their own startup
    /// log, and failing the inner loop on it would be disproportionate.
    /// </summary>
    [Fact]
    public async Task Cors_ConsumerAddDefaultPolicy_InDevelopment_WarnsAndPipelinePolicyWins()
    {
        var capture = new CapturingLoggerProvider();
        await using var app = BuildAppWithConflictingCorsDefault(Environments.Development, capture);

        app.UseApiPipeline(pipeline => pipeline.WithCors());
        app.MapGet("/test", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        capture.Messages.Should().Contain(m => m.Contains("will NOT be applied", StringComparison.Ordinal),
            "the application must be told its default CORS policy was overridden");

        // And the configured policy, not the application's, is the one in force.
        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("Origin", "https://application-chose-this.example");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "the pipeline policy from configuration is what applies");
    }

    /// <summary>
    /// With the pipeline's CORS switched off there is only one configuration claiming the default
    /// policy, so Production startup must not fail — the conflict the guard exists for is absent.
    /// </summary>
    [Fact]
    public async Task Cors_ConsumerAddDefaultPolicy_WithPipelineCorsDisabled_DoesNotFailStartup()
    {
        await using var app = BuildAppWithConflictingCorsDefault(
            Environments.Production, new CapturingLoggerProvider(), pipelineCorsEnabled: false);

        var act = async () => await app.StartAsync();

        await act.Should().NotThrowAsync();
    }

    private static WebApplication BuildAppWithConflictingCorsDefault(
        string environment,
        CapturingLoggerProvider capture,
        bool pipelineCorsEnabled = true)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:Cors:Enabled"] = pipelineCorsEnabled ? "true" : "false";
            c["ApiPipeline:Cors:AllowedOrigins:0"] = "https://configured.example";
        });

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Logging.AddProvider(capture);
        builder.Services.AddRouting();

        // The application configures its own default policy, the documented ASP.NET Core way.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins("https://application-chose-this.example")));

        builder.Services.AddApiPipelineCors(builder.Configuration);

        return builder.Build();
    }
}
