namespace ApiPipeline.NET.RateLimiting;

/// <summary><see cref="HttpContext.Items"/> keys for the policy that applied, read by the header and rejection code.</summary>
internal static class RateLimitContextItems
{
    public static readonly object PolicyName = new();

    public static readonly object PolicyConfig = new();

    /// <summary>The partition type, for rejection telemetry.</summary>
    public static readonly object PartitionType = new();
}
