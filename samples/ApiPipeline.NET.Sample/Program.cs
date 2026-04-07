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

// Must be first — resolves scheme/IP from proxy headers before any other middleware reads them
app.UseApiPipelineForwardedHeaders();

// Before exception handler so correlation ID is available in error responses
app.UseCorrelationId();

// Early in pipeline to catch unhandled exceptions from all downstream middleware
app.UseApiPipelineExceptionHandler();

// After forwarded headers so the correct scheme is used for the redirect
app.UseHttpsRedirection();

// Before authentication so preflight requests are not counted against rate limits
app.UseCors();

// Authentication must run before authorization and before response caching
app.UseAuthentication();

// Authorization MUST precede UseResponseCaching to prevent auth bypass via cached responses
app.UseAuthorization();

// After authorization — only rate-limit real authenticated requests
app.UseRateLimiting();

// Before caching so the compressed form is what gets stored and served
app.UseResponseCompression();

// After authentication + authorization — only cache authorized responses
app.UseResponseCaching();

// Adds security headers via OnStarting; applies to all non-cached responses
app.UseSecurityHeaders();

// Appends Deprecation/Sunset headers for deprecated API versions
app.UseApiVersionDeprecation();

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

app.MapControllers();

app.Run();
