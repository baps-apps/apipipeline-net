namespace ApiPipeline.NET.Observability;

/// <summary>
/// The ids of the package's own compiler diagnostics (<c>[Obsolete]</c> warnings), so a consumer can
/// suppress one by id (<c>&lt;NoWarn&gt;APIPIPELINE001&lt;/NoWarn&gt;</c>) without hiding every
/// obsolete warning.
/// </summary>
internal static class ApiPipelineDiagnostics
{
    /// <summary>Where the ids are documented, one heading each.</summary>
    public const string UrlFormat = "https://github.com/baps-apps/apipipeline-net/blob/main/docs/diagnostics.md";

    /// <summary>Response caching, superseded by output caching.</summary>
    public const string ResponseCachingObsoleteId = "APIPIPELINE001";

    public const string ResponseCachingObsoleteMessage =
        "Response caching is superseded by output caching (WithOutputCaching / AddApiPipelineOutputCaching), " +
        "the server-side cache Microsoft recommends: it caches authenticated responses only when told to, " +
        "supports tags and distributed stores, and its settings come from ApiPipeline:OutputCaching. " +
        "Response caching will be removed in the next major version.";
}
