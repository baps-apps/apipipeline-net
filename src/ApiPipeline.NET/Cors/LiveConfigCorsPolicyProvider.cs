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
    private volatile CorsPolicy _configuredPolicy;
    private readonly CorsPolicy _allowAllPolicy;

    public LiveConfigCorsPolicyProvider(
        IOptionsMonitor<CorsSettings> settingsMonitor,
        IHostEnvironment environment)
    {
        _settingsMonitor = settingsMonitor;
        _environment = environment;
        _allowAllPolicy = new CorsPolicyBuilder()
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .Build();
        _configuredPolicy = BuildConfiguredPolicy(_settingsMonitor.CurrentValue);
        _settingsMonitor.OnChange(settings => _configuredPolicy = BuildConfiguredPolicy(settings));
    }

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var settings = _settingsMonitor.CurrentValue;

        if (_environment.IsDevelopment() && settings.AllowAllInDevelopment)
        {
            return Task.FromResult<CorsPolicy?>(_allowAllPolicy);
        }

        return Task.FromResult<CorsPolicy?>(_configuredPolicy);
    }

    private static CorsPolicy BuildConfiguredPolicy(CorsSettings settings)
    {
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

        var methods = settings.AllowedMethods?
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToArray();
        if (methods is { Length: > 0 })
        {
            if (methods.Contains("*"))
            {
                builder.AllowAnyMethod();
            }
            else
            {
                builder.WithMethods(methods);
            }
        }
        else
        {
            builder.WithMethods(CorsSettings.SafeDefaultAllowedMethods);
        }

        var headers = settings.AllowedHeaders?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToArray();
        if (headers is { Length: > 0 })
        {
            if (headers.Contains("*"))
            {
                builder.AllowAnyHeader();
            }
            else
            {
                builder.WithHeaders(headers);
            }
        }
        else
        {
            builder.WithHeaders(CorsSettings.SafeDefaultAllowedHeaders);
        }

        if (settings.AllowCredentials)
        {
            builder.AllowCredentials();
        }

        return builder.Build();
    }
}
