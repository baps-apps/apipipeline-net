using ApiPipeline.NET.Extensions;
using ApiPipeline.NET.OpenTelemetry;
using Asp.Versioning;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddCorrelationId()
    .AddRateLimiting(builder.Configuration)
    .AddResponseCompression(builder.Configuration)
    .AddResponseCaching(builder.Configuration)
    .AddSecurityHeaders(builder.Configuration)
    .AddCors(builder.Configuration)
    .AddApiVersionDeprecation(builder.Configuration)
    .AddRequestLimits(builder.Configuration)
    .AddForwardedHeaders(builder.Configuration)
    .AddApiPipelineExceptionHandler();

builder.AddApiPipelineObservability();
builder.ConfigureKestrelRequestLimits();

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

app.UseApiPipelineForwardedHeaders();
app.UseCorrelationId();
app.UseApiPipelineExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiting();
app.UseResponseCompression();
app.UseResponseCaching();
app.UseSecurityHeaders();
app.UseApiVersionDeprecation();
app.UseCors();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

app.MapControllers();

app.Run();
