using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Executes the documented <see cref="IEndpointFilter"/> guidance (README, docs/INTERNALS.md) so
/// it cannot drift out of compiling.
/// </summary>
public sealed class EndpointFilterContractTests
{
    private const string TenantHeader = "X-Tenant-Id";
    private const string Detail = "The X-Tenant-Id header is required on this endpoint.";

    /// <summary>The filter as documented in the README, verbatim in behaviour.</summary>
    private sealed class TenantHeaderFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(
            EndpointFilterInvocationContext context,
            EndpointFilterDelegate next)
        {
            if (!context.HttpContext.Request.Headers.ContainsKey(TenantHeader))
            {
                return Results.Problem(detail: Detail, statusCode: StatusCodes.Status400BadRequest);
            }

            return await next(context);
        }
    }

    private static async Task<WebApplication> BuildAsync(string? environment = null)
    {
        var config = TestAppBuilder.MinimalConfig(c =>
        {
            c["ApiPipeline:SecurityHeaders:Enabled"] = "true";
            c["ApiPipeline:ForwardedHeaders:Enabled"] = "false";
        });

        var app = await TestAppBuilder.CreateAppAsync(
            config,
            environment ?? Environments.Production,
            configureServices: s => s.AddConsumerProblemDetails());

        app.UseSimulatedHttps();
        app.UseConsumerErrorHandling();
        app.UseApiPipeline(p => p
            .WithCorrelationId()
            .WithSecurityHeaders());

        // Outside the filtered group on purpose: a readiness probe must not be subject to an
        // application header rule. This is the property the removed middleware could not provide.
        app.MapGet("/health", () => Results.Ok("healthy"));

        app.MapGroup("/api")
            .AddEndpointFilter<TenantHeaderFilter>()
            .MapGet("/orders", () => Results.Ok("orders"));

        await app.StartAsync();
        return app;
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// A rejection returned as <c>Results.Problem</c> must carry the pipeline's own enrichment —
    /// otherwise a consumer following this guidance silently loses the API's error contract, which
    /// is the whole reason the package had an opinion about validation in the first place.
    /// </summary>
    [Fact]
    public async Task Rejection_KeepsTheProblemDetailsContract()
    {
        await using var app = await BuildAsync();

        var response = await app.GetApiTestClient().GetAsync("/api/orders", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = await ReadProblemAsync(response);
        problem.GetProperty("detail").GetString().Should().Be(Detail);
        problem.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// An error body is per-caller and carries ids; an intermediary must never replay it.
    /// </summary>
    [Fact]
    public async Task Rejection_IsNotCacheable()
    {
        await using var app = await BuildAsync();

        var response = await app.GetApiTestClient().GetAsync("/api/orders", TestContext.Current.CancellationToken);

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// The security-headers phase is ordered before everything that can short-circuit. An endpoint
    /// filter short-circuits later than any of those, so this is the weakest case for the ordering
    /// and the one worth pinning.
    /// </summary>
    [Fact]
    public async Task Rejection_StillCarriesSecurityHeaders()
    {
        await using var app = await BuildAsync();

        var response = await app.GetApiTestClient().GetAsync("/api/orders", TestContext.Current.CancellationToken);

        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue();
    }

    /// <summary>
    /// The header rule applies to the group it was attached to and nothing else. The removed
    /// middleware ran for every request in the process, including probes and unmatched paths.
    /// </summary>
    [Fact]
    public async Task Filter_DoesNotApplyOutsideTheGroupItIsAttachedTo()
    {
        await using var app = await BuildAsync();

        var health = await app.GetApiTestClient().GetAsync("/health", TestContext.Current.CancellationToken);

        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A request that satisfies the rule reaches the endpoint untouched.
    /// </summary>
    [Fact]
    public async Task Request_WithTheHeader_ReachesTheEndpoint()
    {
        await using var app = await BuildAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        request.Headers.Add(TenantHeader, "acme");
        var response = await app.GetApiTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("orders");
    }
}
