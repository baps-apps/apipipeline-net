namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>Redis images pinned by multi-platform digest, so a run uses exactly the reviewed server.</summary>
/// <remarks>To update: <c>docker buildx imagetools inspect redis:7.4-alpine</c>, copy the top-level <c>Digest</c>.</remarks>
internal static class TestImages
{
    /// <summary>Redis 7.4, which every test runs on unless it says otherwise.</summary>
    public const string Redis =
        "redis:7.4-alpine@sha256:c5d34a98396a4775e223c74e929162c35c5a2182901ba26b50ade939941ce14b";

    /// <summary>Redis 5.0, the oldest version the package supports.</summary>
    public const string OldestRedis =
        "redis:5.0.14-alpine@sha256:1a3c609295332f1ce603948142a132656c92a08149d7096e203058533c415b8c";
}
