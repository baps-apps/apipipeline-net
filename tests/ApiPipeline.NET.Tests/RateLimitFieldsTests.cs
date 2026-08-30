using ApiPipeline.NET.RateLimiting;
using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The field values, pinned to draft-ietf-httpapi-ratelimit-headers-11: a List of items whose
/// value is the policy name as an sf-string, with <c>q</c>/<c>w</c> on <c>RateLimit-Policy</c> and
/// <c>r</c>/<c>t</c> on <c>RateLimit</c>.
/// </summary>
public sealed class RateLimitFieldsTests
{
    /// <summary>A windowed policy: quota per window, in the default unit (requests).</summary>
    [Fact]
    public void Policy_Windowed_HasQuotaAndWindow() =>
        RateLimitFields.Policy("standard", 100, 60).Should().Be("\"standard\";q=100;w=60");

    /// <summary>
    /// A concurrency policy has no window, and its quota counts requests in flight, which the draft
    /// expresses with the <c>concurrent-requests</c> quota unit rather than the default
    /// <c>requests</c>.
    /// </summary>
    [Fact]
    public void Policy_Concurrency_UsesTheConcurrentRequestsUnit() =>
        RateLimitFields.Policy("uploads", 10, null).Should().Be("\"uploads\";q=10;qu=\"concurrent-requests\"");

    /// <summary>Remaining quota and the effective window in seconds.</summary>
    [Fact]
    public void Status_HasRemainingAndWindow() =>
        RateLimitFields.Status("standard", 0, 42).Should().Be("\"standard\";r=0;t=42");

    /// <summary>The policy name is an sf-string: quotes and backslashes escaped, non-ASCII dropped.</summary>
    [Fact]
    public void PolicyName_IsEscapedAsAnSfString() =>
        RateLimitFields.Policy("a\"b\\cé", 1, 1).Should().Be("\"a\\\"b\\\\c\";q=1;w=1");
}
