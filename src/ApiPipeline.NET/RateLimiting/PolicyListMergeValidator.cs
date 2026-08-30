using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Fails startup when two configuration sources name the same <c>Policies</c> position differently.
/// </summary>
/// <remarks>
/// Configuration merges lists by position: an environment file with <c>[per-user-burst]</c> over a
/// base <c>[strict, permissive]</c> yields <c>per-user-burst</c> with <c>strict</c>'s leftover keys,
/// plus <c>permissive</c>. Needs the configuration root; without one, nothing is checked.
/// </remarks>
internal sealed class PolicyListMergeValidator : IValidateOptions<ApiPipelineRateLimitingOptions>
{
    private readonly IConfigurationSection _section;
    private readonly IConfigurationRoot? _root;

    public PolicyListMergeValidator(IConfigurationSection section, IConfigurationRoot? root)
    {
        _section = section;
        _root = root;
    }

    public ValidateOptionsResult Validate(string? name, ApiPipelineRateLimitingOptions options)
    {
        if (_root is null || !options.Enabled)
        {
            return ValidateOptionsResult.Skip;
        }

        List<string>? failures = null;
        foreach (var entry in _section.GetSection(nameof(ApiPipelineRateLimitingOptions.Policies)).GetChildren())
        {
            var key = ConfigurationPath.Combine(entry.Path, nameof(ApiPipelineRateLimitPolicy.Name));
            (string Name, IConfigurationProvider Source)? first = null;
            foreach (var provider in _root.Providers)
            {
                if (!provider.TryGet(key, out var policyName) || string.IsNullOrWhiteSpace(policyName))
                {
                    continue;
                }

                if (first is not { } earlier)
                {
                    first = (policyName, provider);
                }
                else if (!string.Equals(earlier.Name, policyName, StringComparison.OrdinalIgnoreCase))
                {
                    (failures ??= []).Add(
                        $"{entry.Path} is named '{earlier.Name}' in {earlier.Source} and '{policyName}' in {provider}. " +
                        "Configuration merges lists by position, not by name, so the later source renames the " +
                        "earlier policy, inheriting every setting it leaves out, and the earlier source's other " +
                        "policies stay in effect. Restate the same policies in the same order and override only " +
                        "their values, or define the list in one source only.");
                    break;
                }
            }
        }

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
