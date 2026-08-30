using ApiPipeline.NET.Pipeline;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>The framework's default request timeout policy, while enabled.</summary>
internal sealed class ConfigureRequestTimeoutOptions : IConfigureOptions<RequestTimeoutOptions>
{
    private readonly IOptions<ApiPipelineRequestTimeoutsOptions> _settings;

    public ConfigureRequestTimeoutOptions(IOptions<ApiPipelineRequestTimeoutsOptions> settings)
        => _settings = settings;

    public void Configure(RequestTimeoutOptions options)
    {
        var settings = _settings.Value;
        if (!settings.Enabled)
        {
            return;
        }

        options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = settings.DefaultTimeout,
            TimeoutStatusCode = settings.TimeoutStatusCode,
            // The middleware has set the status (TimeoutStatusCode) by the time this runs.
            WriteTimeoutResponse = static context => ProblemResponse.WriteAsync(
                context,
                context.Response.StatusCode,
                type: null,
                title: null,
                detail: "The request did not complete within the time allowed."),
        };
    }
}
