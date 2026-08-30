namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>What a policy does while Redis cannot decide (unreachable, slow, failing, or circuit open).</summary>
public enum RedisFallbackBehavior
{
    /// <summary>Each pod limits on its own; with N replicas a caller can get N times the limit. Default.</summary>
    PerPod,

    /// <summary>No limit until Redis is back. For capacity-only policies.</summary>
    Allow,

    /// <summary>
    /// Reject (429): fail closed, for abuse protection such as login or OTP. Not gated by the circuit:
    /// asks Redis every time and rejects only when its own call fails.
    /// </summary>
    Reject,
}
