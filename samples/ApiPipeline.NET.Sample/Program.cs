using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.OpenTelemetry;
using ApiPipeline.NET.Versioning;
using Asp.Versioning;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddCorrelationId()                          // Generates/validates X-Correlation-Id on every request and response
    .AddRateLimiting(builder.Configuration)      // Configures fixed-window rate limiting to protect against traffic bursts
    .AddResponseCompression(builder.Configuration) // Enables Brotli/gzip compression for eligible responses
    .AddResponseCaching(builder.Configuration)   // Enables server-side response caching for GET/HEAD responses
    .AddSecurityHeaders(builder.Configuration)   // Registers Strict-Transport-Security, X-Content-Type-Options, and Referrer-Policy headers
    .AddCors(builder.Configuration)              // Registers CORS policies (AllowAll in development, configured in production)
    .AddApiPipelineVersioning(builder.Configuration) // Adds Deprecation/Sunset headers for deprecated API versions (via Asp.Versioning.Mvc satellite)
    .AddRequestLimits(builder.Configuration)     // Sets maximum request body size limits
    .AddForwardedHeaders(builder.Configuration)  // Configures trusted proxies for X-Forwarded-For/Proto/Host processing
    .AddApiPipelineExceptionHandler();           // Registers RFC 7807 Problem Details error responses with correlation ID

// Wires up OpenTelemetry tracing, metrics, and logging exporters
builder.AddApiPipelineObservability();

builder.Services.AddAuthentication();            // Registers the ASP.NET Core authentication services
builder.Services.AddAuthorization();             // Registers the ASP.NET Core authorization services
builder.Services.AddControllers();               // Registers MVC controller services
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0); // Fall back to v1.0 when no version is specified
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;                 // Include api-supported-versions header in responses
        options.ApiVersionReader = new UrlSegmentApiVersionReader(); // Read version from URL path, e.g. /api/v1/orders
    });

var app = builder.Build();

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithExceptionHandler()
    .WithHttpsRedirection()
    .WithCors()
    .WithAuthentication()
    .WithAuthorization()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithSecurityHeaders()
    .WithVersionDeprecation()
);

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

app.MapControllers();

app.Run();
