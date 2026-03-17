using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options for Kestrel server limits and ASP.NET Core form request limits.
/// When <see cref="Enabled"/> is <c>false</c>, no limits are applied.
/// </summary>
public sealed class RequestLimitsOptions
{
    /// <summary>Enables Kestrel/form request limits when true.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Maps to Kestrel <c>MaxRequestBodySize</c> and form multipart/body buffer limits.</summary>
    [Range(0, long.MaxValue)]
    public long? MaxRequestBodySize { get; set; }

    /// <summary>Maps to Kestrel <c>MaxRequestHeadersTotalSize</c>.</summary>
    [Range(0, int.MaxValue)]
    public int? MaxRequestHeadersTotalSize { get; set; }

    /// <summary>Maps to Kestrel <c>MaxRequestHeaderCount</c>.</summary>
    [Range(0, int.MaxValue)]
    public int? MaxRequestHeaderCount { get; set; }

    /// <summary>Maps to ASP.NET Core <c>FormOptions.ValueCountLimit</c>.</summary>
    [Range(0, int.MaxValue)]
    public int? MaxFormValueCount { get; set; }
}
