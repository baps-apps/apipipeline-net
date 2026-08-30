// A Native AOT app that registers and runs every ApiPipeline.NET feature, so publishing it checks
// the packages for trimming and AOT warnings, and running it checks the features still work once
// trimmed (configuration binding and validation are source-generated, and must not need reflection).
using Asp.Versioning;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddApiPipeline(builder.Configuration);
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.ReportApiVersions = true;
});
builder.Services.AddApiPipelineVersionDeprecation(builder.Configuration);
builder.Services.AddApiPipelineRedisRateLimiting(builder.Configuration);

var app = builder.Build();

app.UseApiPipeline(pipeline => pipeline
    .WithForwardedHeaders()
    .WithCorrelationId()
    .WithSecurityHeaders()
    .WithRequestTimeouts()
    .WithCors()
    .WithEdgeRateLimiting()
    .WithRateLimiting()
    .WithResponseCompression()
    .WithResponseCaching()
    .WithRequestSizeTracking());

app.MapGet("/health", () => "ok");

// Two requests a minute ("smoke" policy), so the script sees a 429; shared when Redis is enabled.
app.MapGet("/limited", () => "limited").RequireRateLimiting("smoke");

var orders = app.NewVersionedApi("Orders");
orders.MapGet("/api/v{version:apiVersion}/orders", () => "orders")
    .HasApiVersion(new ApiVersion(1, 0))
    .HasApiVersion(new ApiVersion(2, 0));

app.Run();
