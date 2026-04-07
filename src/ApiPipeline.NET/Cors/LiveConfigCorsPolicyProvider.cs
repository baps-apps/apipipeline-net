using ApiPipeline.NET.Observability;
using ApiPipeline.NET.Options;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Cors;

/// <summary>
/// An <see cref="ICorsPolicyProvider"/> that evaluates CORS policy from
/// <see cref="IOptionsMonitor{CorsSettings}"/> on each request, enabling hot-reload
/// of allowed origins without an application restart.
/// </summary>
internal sealed class LiveConfigCorsPolicyProvider : ICorsPolicyProvider
{
    private readonly IOptionsMonitor<CorsSettings> _settingsMonitor;
    private readonly IHostEnvironment _environment;

    public LiveConfigCorsPolicyProvider(
        IOptionsMonitor<CorsSettings> settingsMonitor,
        IHostEnvironment environment)
    {
        _settingsMonitor = settingsMonitor;
        _environment = environment;
    }

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var settings = _settingsMonitor.CurrentValue;

        if (_environment.IsDevelopment() && settings.AllowAllInDevelopment)
        {
            var allowAll = new CorsPolicyBuilder()
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
                .Build();
            return Task.FromResult<CorsPolicy?>(allowAll);
        }

        var builder = new CorsPolicyBuilder();

        if (settings.AllowedOrigins is { Length: > 0 })
        {
            builder.WithOrigins(settings.AllowedOrigins);
        }
        else
        {
            builder.SetIsOriginAllowed(origin =>
            {
                ApiPipelineTelemetry.RecordCorsRejected();
                return false;
            });
        }

        if (settings.AllowedMethods is { Length: > 0 } && !settings.AllowedMethods.Contains("*"))
        {
            builder.WithMethods(settings.AllowedMethods);
        }
        else
        {
            builder.AllowAnyMethod();
        }

        if (settings.AllowedHeaders is { Length: > 0 } && !settings.AllowedHeaders.Contains("*"))
        {
            builder.WithHeaders(settings.AllowedHeaders);
        }
        else
        {
            builder.AllowAnyHeader();
        }

        if (settings.AllowCredentials)
        {
            builder.AllowCredentials();
        }

        return Task.FromResult<CorsPolicy?>(builder.Build());
    }
}
