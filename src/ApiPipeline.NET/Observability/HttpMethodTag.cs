namespace ApiPipeline.NET.Observability;

/// <summary>The <c>http.request.method</c> tag: known methods, else <c>_OTHER</c> (OpenTelemetry), so callers cannot create series.</summary>
internal static class HttpMethodTag
{
    public const string Other = "_OTHER";

    /// <summary>The canonical name of an RFC 9110 method or <c>PATCH</c>, else <see cref="Other"/>.</summary>
    public static string Normalize(string? method) => method switch
    {
        null or "" => Other,
        _ when HttpMethods.IsGet(method) => HttpMethods.Get,
        _ when HttpMethods.IsPost(method) => HttpMethods.Post,
        _ when HttpMethods.IsPut(method) => HttpMethods.Put,
        _ when HttpMethods.IsDelete(method) => HttpMethods.Delete,
        _ when HttpMethods.IsPatch(method) => HttpMethods.Patch,
        _ when HttpMethods.IsHead(method) => HttpMethods.Head,
        _ when HttpMethods.IsOptions(method) => HttpMethods.Options,
        _ when HttpMethods.IsTrace(method) => HttpMethods.Trace,
        _ when HttpMethods.IsConnect(method) => HttpMethods.Connect,
        _ => Other,
    };
}
