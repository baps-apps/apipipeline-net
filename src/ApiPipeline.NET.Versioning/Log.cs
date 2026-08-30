using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Every log line the Versioning package writes, source-generated with
/// <see cref="LoggerMessageAttribute"/>. Event ids 200-299 (the core package uses 1-99, the Redis
/// add-on 100-199).
/// </summary>
internal static partial class Log
{
    [LoggerMessage(200, LogLevel.Warning,
        "ApiPipeline.NET.Versioning 2.0 reads deprecation dates from '{Section}:{Key}', but this " +
        "configuration still has the 1.x '{Legacy}' section, which is no longer read. No " +
        "deprecation or sunset policy is applied. Move the section to '{Section}:{Key}'.")]
    public static partial void LegacyVersionDeprecationSection(this ILogger logger, string section, string key, string legacy);

    [LoggerMessage(201, LogLevel.Warning,
        "API version deprecation policies are configured, but ApiVersioningOptions." +
        "ReportApiVersions is false. Deprecation, Sunset and Link headers are emitted only " +
        "for endpoints whose version set calls ReportApiVersions(); every other endpoint " +
        "will emit nothing. Set ReportApiVersions = true in AddApiVersioning(...) to apply " +
        "it everywhere, call ReportApiVersions() on the version sets that need it, or set " +
        "ApiPipeline:VersionDeprecation:Enabled to false.")]
    public static partial void VersionReportingOff(this ILogger logger);
}
