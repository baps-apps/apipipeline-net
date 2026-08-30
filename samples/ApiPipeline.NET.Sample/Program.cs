using ApiPipeline.NET.Correlation;
using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Versioning;
using ApiPipeline.NET.Sample;
using Asp.Versioning;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Every feature binds a child of the "ApiPipeline" section (ApiPipeline:Cors, ApiPipeline:RateLimiting,
// ...; names in ApiPipelineConfigurationKeys).
// ConfigurationSnippets/*.json has copy-paste examples for common scenarios.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiPipeline(builder.Configuration);

// Error bodies belong to the application. This puts the pipeline's ids into every ProblemDetails
// body, so a pasted error is searchable.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        // Error bodies carry per-request ids; never cache them.
        context.HttpContext.Response.Headers.CacheControl = "no-store";

        if (context.HttpContext.GetCorrelationId() is { Length: > 0 } correlationId)
        {
            context.ProblemDetails.Extensions["correlationId"] = correlationId;
        }

        // Overwrites ASP.NET Core's "traceId" (a full traceparent) with the bare trace id that
        // matches X-Trace-Id.
        if (context.HttpContext.GetTraceId() is { Length: > 0 } traceId)
        {
            context.ProblemDetails.Extensions["traceId"] = traceId;
        }
    };
});

// OpenTelemetry is configured by the application. AddMeter(MeterName) exports ApiPipeline.NET's
// metrics; without it they never reach a backend.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(ApiPipelineTelemetry.MeterName)
        .AddConsoleExporter());

// Forwards the incoming X-Correlation-Id on outbound calls (traceparent propagates on its own).
builder.Services
    .AddHttpClient("downstream", client => client.BaseAddress = new Uri("https://example.invalid"))
    .AddApiPipelineCorrelationIdPropagation();

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        // Required for deprecation headers.
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    // Required for [MapToApiVersion]; without it GetV1 and GetV2 are ambiguous.
    .AddMvc();

// Deprecation and sunset dates from configuration. Order relative to AddApiVersioning does not matter.
builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);

var app = builder.Build();

// Phases run in the order called (checked against the recommended order at startup, with a
// warning per broken rule); WithMiddleware(...) adds your own at any position; omit a With* to
// leave it out.
app.UseApiPipeline(pipeline => pipeline
    // Outside Development, a security phase below whose feature is off fails startup instead of
    // running without that protection.
    .RequireSecurityFeaturesEnabled()
    .WithForwardedHeaders()
    .WithRequestSizeTracking()
    .WithCorrelationId()
    .WithSecurityHeaders()
    // Error handling right after the id and header phases: exceptions and bare status codes from
    // every later phase (429, 401, ...) become RFC 9457 bodies, and the unhandled-exception log
    // line is written inside the correlation scope, so it carries the CorrelationId.
    .WithMiddleware(a =>
    {
        a.UseExceptionHandler();
        a.UseStatusCodePages();
    })
    // Streaming endpoints opt out with .DisableRequestTimeout().
    .WithRequestTimeouts()
    .WithHttpsRedirection()
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithOutputCaching());

// Probe paths (/health, /internal/health and everything under them) are never rate limited by
// default (ApiPipeline:RateLimiting:ExcludedPaths): a probe answered with 429 restarts a healthy pod.
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

// Both ids are also on every response as X-Correlation-Id / X-Trace-Id.
app.MapGet("/whoami", (HttpContext ctx) => Results.Ok(new
{
    CorrelationId = ctx.GetCorrelationId(),
    TraceId = ctx.GetTraceId()
}));

// Request checks are endpoint filters, scoped to the routes that need them (not /health). For MVC:
//     app.MapControllers()
//        .AddEndpointFilter<ControllerActionEndpointConventionBuilder, SampleTenantHeaderFilter>();
// Left off controllers here so /api/v1/orders works with a bare curl.
app.MapGroup("/tenanted")
    .AddEndpointFilter<SampleTenantHeaderFilter>()
    .MapGet("/ping", () => Results.Ok("ok"));

app.MapControllers();

app.Run();
