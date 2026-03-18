using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET.Options;

/// <summary>
/// Configuration options for HTTP response compression.
/// </summary>
public sealed class ResponseCompressionSettings
{
    /// <summary>
    /// Indicates whether response compression is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Indicates whether compression is applied to HTTPS responses.
    /// <para>
    /// <b>Security note:</b> enabling compression over HTTPS can expose your application to
    /// CRIME/BREACH side-channel attacks if responses contain secrets alongside attacker-controlled
    /// content. For APIs that never reflect user input in the same response as secrets, the risk
    /// is low. Disable this property if your API returns sensitive tokens or secrets in response bodies.
    /// </para>
    /// </summary>
    public bool EnableForHttps { get; set; } = true;

    /// <summary>
    /// Indicates whether Brotli compression is enabled.
    /// </summary>
    public bool EnableBrotli { get; set; } = true;

    /// <summary>
    /// Indicates whether Gzip compression is enabled.
    /// </summary>
    public bool EnableGzip { get; set; } = true;

    /// <summary>
    /// An optional whitelist of MIME types that are eligible for compression.
    /// </summary>
    [MinLength(0)]
    public string[]? MimeTypes { get; set; }

    /// <summary>
    /// An optional list of MIME types that should never be compressed.
    /// </summary>
    [MinLength(0)]
    public string[]? ExcludedMimeTypes { get; set; }

    /// <summary>
    /// Paths that should be excluded from response compression.
    /// </summary>
    [MinLength(0)]
    public string[]? ExcludedPaths { get; set; } = ["/health"];
}

