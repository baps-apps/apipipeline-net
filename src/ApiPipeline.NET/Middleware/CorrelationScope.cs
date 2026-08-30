using System.Collections;
using System.Globalization;
using ApiPipeline.NET.Correlation;

namespace ApiPipeline.NET.Middleware;

/// <summary>
/// The request's logging scope (<c>CorrelationId</c> only; the host already adds <c>TraceId</c>), and
/// the state of the header callback, so a request costs one allocation.
/// </summary>
internal sealed class CorrelationScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    private string? _text;

    public CorrelationScope(HttpContext context, string correlationId, string? traceId, bool emitTraceId)
    {
        Context = context;
        CorrelationId = correlationId;
        TraceId = traceId;
        EmitTraceId = emitTraceId;
    }

    /// <summary>Read only by the header callback, during the request.</summary>
    public HttpContext Context { get; }

    public string CorrelationId { get; }

    public string? TraceId { get; }

    public bool EmitTraceId { get; }

    public int Count => 1;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new(CorrelationHeaders.CorrelationIdLogProperty, CorrelationId),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return this[0];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        _text ??= string.Create(CultureInfo.InvariantCulture, $"CorrelationId:{CorrelationId}");
}
