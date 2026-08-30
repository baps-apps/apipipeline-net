using System.Globalization;
using System.Text;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>
/// Formats the IETF rate-limit fields (draft-ietf-httpapi-ratelimit-headers-11, not yet an RFC):
/// <c>RateLimit-Policy: "strict";q=100;w=60</c> and <c>RateLimit: "strict";r=0;t=60</c>.
/// </summary>
internal static class RateLimitFields
{
    public const string PolicyHeader = "RateLimit-Policy";

    public const string StatusHeader = "RateLimit";

    /// <summary>A <c>RateLimit-Policy</c> value; concurrency omits <c>w</c> and states <c>qu="concurrent-requests"</c>.</summary>
    public static string Policy(string policyName, int quota, int? windowSeconds) =>
        windowSeconds is { } window
            ? string.Create(CultureInfo.InvariantCulture, $"{Name(policyName)};q={quota};w={window}")
            : string.Create(CultureInfo.InvariantCulture, $"{Name(policyName)};q={quota};qu=\"concurrent-requests\"");

    /// <summary>A <c>RateLimit</c> value.</summary>
    public static string Status(string policyName, int remaining, int resetSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{Name(policyName)};r={remaining};t={resetSeconds}");

    /// <summary>The name as an RFC 9651 string: printable ASCII, <c>"</c> and <c>\</c> escaped, anything else dropped.</summary>
    private static string Name(string policyName)
    {
        var builder = new StringBuilder(policyName.Length + 2).Append('"');
        foreach (var c in policyName)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\').Append(c);
            }
            else if (c is >= ' ' and <= '~')
            {
                builder.Append(c);
            }
        }

        return builder.Append('"').ToString();
    }
}
