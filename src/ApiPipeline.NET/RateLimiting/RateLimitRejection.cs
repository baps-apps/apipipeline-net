using System.Globalization;
using ApiPipeline.NET.Pipeline;

namespace ApiPipeline.NET.RateLimiting;

/// <summary>Writes the 429 for both limiters, so every rejection looks the same.</summary>
internal static class RateLimitRejection
{
    /// <summary>The RFC 9457 <c>type</c> of a 429.</summary>
    internal const string ProblemType = "https://www.rfc-editor.org/rfc/rfc6585#section-4";

    /// <summary>
    /// Sets status, <c>Cache-Control</c>, <c>Retry-After</c> and optionally the rate-limit fields, then
    /// writes an RFC 9457 body. Does nothing once the response has started.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="policyName">The rejecting policy, if known.</param>
    /// <param name="retryAfterSeconds">Seconds until a retry, if known.</param>
    /// <param name="permitLimit">The limit; the fields are omitted when unknown.</param>
    /// <param name="windowSeconds">The window, if known.</param>
    /// <param name="emitQuotaHeaders">Whether to send the rate-limit fields.</param>
    internal static Task WriteAsync(
        HttpContext context,
        string? policyName,
        int? retryAfterSeconds,
        int? permitLimit,
        int? windowSeconds,
        bool emitQuotaHeaders)
    {
        var response = context.Response;

        if (response.HasStarted)
        {
            return Task.CompletedTask;
        }

        WriteHeaders(response, policyName, retryAfterSeconds, permitLimit, windowSeconds, emitQuotaHeaders);

        // No policy name: it would disclose what the withheld headers hide.
        return ProblemResponse.WriteAsync(
            context,
            StatusCodes.Status429TooManyRequests,
            ProblemType,
            "Too Many Requests",
            retryAfterSeconds is { } seconds
                ? string.Create(CultureInfo.InvariantCulture, $"The rate limit was exceeded. Retry after {seconds} seconds.")
                : "The rate limit was exceeded. Retry later.");
    }

    private static void WriteHeaders(
        HttpResponse response,
        string? policyName,
        int? retryAfterSeconds,
        int? permitLimit,
        int? windowSeconds,
        bool emitQuotaHeaders)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;

        // A cached 429 would outlive the quota window, or reach another client.
        response.Headers.CacheControl = "no-store";

        if (retryAfterSeconds is { } retryAfter)
        {
            response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        if (!emitQuotaHeaders || permitLimit is not { } limit || string.IsNullOrEmpty(policyName))
        {
            return;
        }

        response.Headers[RateLimitFields.PolicyHeader] = RateLimitFields.Policy(policyName, limit, windowSeconds);

        if (retryAfterSeconds is { } reset)
        {
            response.Headers[RateLimitFields.StatusHeader] = RateLimitFields.Status(policyName, 0, reset);
        }
    }
}
