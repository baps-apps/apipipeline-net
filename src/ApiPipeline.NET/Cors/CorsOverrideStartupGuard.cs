using ApiPipeline.NET.Configuration;
using ApiPipeline.NET.Observability;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Cors;

/// <summary>
/// While CORS is on, reports setups where the pipeline's policy and the application's override each
/// other: an application default policy, or a provider registered after the pipeline's, fail
/// startup outside Development; a provider registered before it is wrapped and logs a warning.
/// </summary>
internal sealed class CorsOverrideStartupGuard : IApiPipelineStartupCheck
{
    internal const string Message =
        "CORS: this application registered its own default policy via AddCors(o => o.AddDefaultPolicy(...)), " +
        "but ApiPipeline.NET drives CORS from the ApiPipeline:Cors configuration section and answers the " +
        "default policy lookup. That registered default policy will NOT be applied. Move the origins, " +
        "methods, and headers into ApiPipeline:Cors in configuration, or register the policy under a name and " +
        "select it with RequireCors(\"name\") / [EnableCors(\"name\")] — named policies are still honoured. " +
        "To keep your own default policy instead, set ApiPipeline:Cors:Enabled to false — the pipeline's " +
        "provider then hands every lookup to the application's provider, so your policy applies unchanged. " +
        "That is the only switch, and it is in configuration where an operator can see it.";

    internal const string ProviderRegisteredAfterMessage =
        "CORS: ApiPipeline:Cors:Enabled is true, but an ICorsPolicyProvider ({ProviderType}) was registered " +
        "after AddApiPipeline / AddApiPipelineCors and replaces the pipeline's, so ApiPipeline:Cors is NOT " +
        "applied. Register your provider before AddApiPipeline: the pipeline then wraps it, answering the " +
        "default lookup from ApiPipeline:Cors and handing named lookups to your provider. Or set " +
        "ApiPipeline:Cors:Enabled to false to let your provider answer everything.";

    internal const string CustomProviderWrappedMessage =
        "CORS: the application's own ICorsPolicyProvider ({ProviderType}) keeps answering named policy " +
        "lookups, but ApiPipeline:Cors:Enabled is true, so the default (unnamed) lookup is answered from " +
        "ApiPipeline:Cors instead. Set ApiPipeline:Cors:Enabled to false if your provider should answer it.";

    private readonly IOptions<CorsOptions> _corsOptions;
    private readonly IOptions<ApiPipelineCorsOptions> _settings;
    private readonly IHostEnvironment _environment;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CorsOverrideStartupGuard> _logger;

    public CorsOverrideStartupGuard(
        IOptions<CorsOptions> corsOptions,
        IOptions<ApiPipelineCorsOptions> settings,
        IHostEnvironment environment,
        IServiceScopeFactory scopes,
        ILogger<CorsOverrideStartupGuard> logger)
    {
        _corsOptions = corsOptions;
        _settings = settings;
        _environment = environment;
        _scopes = scopes;
        _logger = logger;
    }

    public void Run()
    {
        if (!_settings.Value.Enabled)
        {
            return;
        }

        CheckProviderInEffect();
        CheckApplicationDefaultPolicy();
    }

    private void CheckProviderInEffect()
    {
        // In a scope: the provider may be scoped.
        using var scope = _scopes.CreateScope();
        var provider = scope.ServiceProvider.GetService<ICorsPolicyProvider>();

        if (provider is not ConfiguredCorsPolicyProvider wrapper)
        {
            var type = provider?.GetType().FullName ?? "none";
            if (StrictEnvironment.Applies(_environment))
            {
                throw new InvalidOperationException(
                    ProviderRegisteredAfterMessage.Replace("{ProviderType}", type, StringComparison.Ordinal) +
                    $" (Environment {StrictEnvironment.Describe(_environment)}: this check fails startup in " +
                    "every environment except Development.)");
            }

            _logger.CorsProviderRegisteredAfter(type);
            return;
        }

        if (wrapper.Inner is not DefaultCorsPolicyProvider)
        {
            _logger.CorsCustomProviderWrapped(wrapper.Inner.GetType().FullName ?? wrapper.Inner.GetType().Name);
        }
    }

    private void CheckApplicationDefaultPolicy()
    {
        CorsPolicy? applicationDefault;
        try
        {
            applicationDefault = _corsOptions.Value.GetPolicy(_corsOptions.Value.DefaultPolicyName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return;
        }

        if (applicationDefault is null)
        {
            return;
        }

        if (StrictEnvironment.Applies(_environment))
        {
            throw new InvalidOperationException(
                $"{Message} (Environment {StrictEnvironment.Describe(_environment)}: this check fails " +
                "startup in every environment except Development.)");
        }

        _logger.CorsDefaultPolicyOverridden();
    }
}
