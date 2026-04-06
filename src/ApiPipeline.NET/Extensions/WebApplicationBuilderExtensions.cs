namespace ApiPipeline.NET.Extensions;

/// <summary>
/// Extension methods for configuring <see cref="WebApplicationBuilder"/> with API pipeline features.
/// </summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>
    /// <para><b>Deprecated.</b> Kestrel request limits are now applied automatically via
    /// <see cref="ServiceCollectionExtensions.AddRequestLimits"/>, which registers
    /// an <c>IConfigureOptions&lt;KestrelServerOptions&gt;</c> backed by validated options.</para>
    /// <para>Remove this call — it is now a no-op.</para>
    /// </summary>
    [Obsolete(
        "ConfigureKestrelRequestLimits is no longer needed. Request limits are applied automatically " +
        "when AddRequestLimits is called. Remove this call from your startup code.",
        error: false)]
    public static WebApplicationBuilder ConfigureKestrelRequestLimits(this WebApplicationBuilder builder)
    {
        // No-op: limits are now configured via IConfigureOptions<KestrelServerOptions>
        // registered by AddRequestLimits. This method is retained for backwards compatibility.
        return builder;
    }
}
