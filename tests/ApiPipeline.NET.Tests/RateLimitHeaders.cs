using System.Globalization;
using System.Text.RegularExpressions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Reads the IETF rate-limit fields (draft-ietf-httpapi-ratelimit-headers):
/// <c>RateLimit-Policy: "name";q=10;w=60</c> and <c>RateLimit: "name";r=0;t=60</c>.
/// </summary>
internal static partial class RateLimitHeaders
{
    public const string PolicyHeader = "RateLimit-Policy";
    public const string StatusHeader = "RateLimit";

    /// <summary>Every <c>RateLimit-Policy</c> field value on the response.</summary>
    public static string[] PolicyValues(HttpResponseMessage response) =>
        response.Headers.TryGetValues(PolicyHeader, out var values) ? [.. values] : [];

    /// <summary>The <c>q</c> (quota) parameter of the single <c>RateLimit-Policy</c> value.</summary>
    public static int? Quota(HttpResponseMessage response) => Parameter(PolicyValues(response), "q");

    /// <summary>The <c>w</c> (window, seconds) parameter of the single <c>RateLimit-Policy</c> value.</summary>
    public static int? Window(HttpResponseMessage response) => Parameter(PolicyValues(response), "w");

    /// <summary>The policy name (the quoted string item) of the <c>RateLimit-Policy</c> value.</summary>
    public static string? PolicyName(HttpResponseMessage response) =>
        PolicyValues(response).SingleOrDefault() is { } value && NameRegex().Match(value) is { Success: true } m
            ? m.Groups[1].Value
            : null;

    /// <summary>The single <c>RateLimit</c> field value, or <c>null</c>.</summary>
    public static string? Status(HttpResponseMessage response) =>
        response.Headers.TryGetValues(StatusHeader, out var values) ? values.Single() : null;

    private static int? Parameter(string[] values, string name)
    {
        if (values.Length != 1)
        {
            return null;
        }

        var match = Regex.Match(values[0], ";" + name + "=(\\d+)", RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex("^\"([^\"]*)\"")]
    private static partial Regex NameRegex();
}
