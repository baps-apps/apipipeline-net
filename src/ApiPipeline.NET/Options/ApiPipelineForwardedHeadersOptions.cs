using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>
/// Forwarded headers settings, section <c>ApiPipeline:ForwardedHeaders</c>. Outside Development,
/// set <see cref="KnownProxies"/> or <see cref="KnownNetworks"/>.
/// </summary>
public sealed class ApiPipelineForwardedHeadersOptions : IValidatableObject
{
    /// <summary>Whether <c>X-Forwarded-*</c> headers are applied. Default <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Proxy entries read from <c>X-Forwarded-For</c>: the number of trusted proxies in front of the
    /// app. Default <c>1</c>; at most 20.
    /// </summary>
    [Range(1, 20)]
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Trusted proxy addresses: dotted-decimal IPv4 or IPv6. Empty trusts only loopback. An entry that
    /// is not an address fails startup.
    /// </summary>
    public IList<string>? KnownProxies { get; set; }

    /// <summary>Trusted proxy networks, as CIDR ranges. An entry that is not one fails startup.</summary>
    public IList<string>? KnownNetworks { get; set; }

    /// <summary>Removes the default loopback trust before applying the lists above.</summary>
    public bool ClearDefaultProxies { get; set; }

    /// <summary>
    /// Fails startup outside Development for an empty trust set (every caller trusted), a
    /// loopback-only one (the real proxy ignored), or networks that, alone or together, contain every
    /// client address (<c>0.0.0.0/0</c>, <c>::/0</c>, <c>0.0.0.0/1</c> plus <c>128.0.0.0/1</c>).
    /// Default <c>true</c>. Development only warns.
    /// </summary>
    public bool EnforceTrustedProxyConfiguration { get; set; } = true;

    /// <summary>
    /// Applies <c>X-Forwarded-Host</c>. Default <c>false</c>: most ingresses copy the caller's
    /// <c>Host</c> into it. Requires a real <c>AllowedHosts</c> list outside Development, and only a
    /// host on it is applied.
    /// </summary>
    public bool ProcessHost { get; set; }

    /// <summary>
    /// A bad trust entry fails, in every environment: skipped, it would leave a narrower trust set
    /// than written, and the proxy it meant would be every client's address.
    /// </summary>
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        foreach (var proxy in KnownProxies ?? [])
        {
            if (!IPText.TryParseAddress(proxy, out _))
            {
                yield return new ValidationResult(
                    $"KnownProxies entry '{proxy}' is not an IP address. Use one address, for example " +
                    "\"10.0.10.5\" or \"fd00::5\", with IPv4 written as four decimal numbers (\"10.0.10\" " +
                    "and \"010.0.0.1\" are read as other addresses). For a range, use KnownNetworks.",
                    [nameof(KnownProxies)]);
            }
        }

        foreach (var failure in ProbePaths.ValidateNetworks(KnownNetworks, nameof(KnownNetworks)))
        {
            yield return failure;
        }
    }
}
