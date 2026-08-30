using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>Correlation id settings, section <c>ApiPipeline:CorrelationId</c>.</summary>
public sealed class ApiPipelineCorrelationIdOptions
{
    /// <summary>
    /// Adopts a well-formed inbound <c>X-Correlation-Id</c>. Default <c>true</c>. Does not cover the
    /// trace id, which ASP.NET Core continues from <c>traceparent</c>; strip that at an untrusted edge.
    /// </summary>
    public bool AcceptInboundCorrelationId { get; set; } = true;

    /// <summary>Sends <c>X-Trace-Id</c> with the request's W3C trace id. Default <c>true</c>.</summary>
    public bool EmitTraceIdHeader { get; set; } = true;

    /// <summary>Longest inbound id accepted; longer ones are replaced. Default 128.</summary>
    [Range(8, 256)]
    public int MaxLength { get; set; } = 128;
}
