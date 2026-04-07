using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.OpenTelemetry;
using ApiPipeline.NET.Sample;
using ApiPipeline.NET.Versioning;
using Asp.Versioning;

// =============================================================================
// Options pattern
// -----------------------------------------------------------------------------
// Each Add*(IConfiguration) call binds a named JSON section. Section names are the
// same as in ApiPipeline.NET.Configuration.ApiPipelineConfigurationKeys (see README).
// Combine appsettings.json + appsettings.{Environment}.json + environment variables.
//
// ConfigurationSnippets/*.json in this project are copy-paste examples for common
// scenarios (minimal, ingress, anonymous IP fallback, output-cache migration).
// =============================================================================
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddCorrelationId()
    .AddRateLimiting(builder.Configuration)
    .AddResponseCompression(builder.Configuration)
    .AddResponseCaching(builder.Configuration)
    .AddSecurityHeaders(builder.Configuration)
    .AddCors(builder.Configuration)
    .AddApiPipelineVersioning(builder.Configuration)
    .AddRequestLimits(builder.Configuration)
    .AddForwardedHeaders(builder.Configuration)
    .AddRequestSizeTracking()
    .AddRequestValidation<SampleRequestValidationFilter>()
    .AddApiPipelineExceptionHandler();

builder.AddApiPipelineObservability();

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    });

var app = builder.Build();

// Phase-enforced pipeline (order is fixed regardless of With* call order in the lambda).
// Register WithRequestValidation only when AddRequestValidation<T> was called above.
//
// Request body size histogram: AddRequestSizeTracking registers the middleware type; for ideal
// ordering (immediately after forwarded headers), build the early middleware manually — see
// README "Scenario: Request body metrics".
app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .WithHttpsRedirection()
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRequestValidation()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithSecurityHeaders()
    .WithVersionDeprecation());

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

app.MapControllers();

app.Run();
