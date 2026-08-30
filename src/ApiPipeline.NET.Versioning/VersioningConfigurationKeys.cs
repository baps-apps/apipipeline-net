namespace ApiPipeline.NET.Versioning;

/// <summary>Configuration section names of <c>ApiPipeline.NET.Versioning</c>.</summary>
public static class VersioningConfigurationKeys
{
    /// <summary>The top-level section: <c>ApiPipeline</c>.</summary>
    public const string Section = "ApiPipeline";

    /// <summary>The deprecation section: <c>ApiPipeline:VersionDeprecation</c>.</summary>
    public const string ApiVersionDeprecation = "VersionDeprecation";

    /// <summary>A top-level name the package does not read; read only to warn.</summary>
    internal const string LegacyApiVersionDeprecation = "ApiVersionDeprecationOptions";
}
