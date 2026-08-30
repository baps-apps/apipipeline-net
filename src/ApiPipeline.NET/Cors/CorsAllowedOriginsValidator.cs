using System.Globalization;
using ApiPipeline.NET.Configuration;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Cors;

/// <summary>
/// Fails startup for allowed origins that can never match a browser's <c>Origin</c> (trailing
/// slash, path, default port, wildcard pattern), the <c>null</c> origin, <c>*</c> mixed with others,
/// and, with credentials outside Development, <c>http://</c> origins.
/// </summary>
internal sealed class CorsAllowedOriginsValidator : IValidateOptions<ApiPipelineCorsOptions>
{
    private const string Key = "ApiPipeline:Cors:AllowedOrigins";

    private readonly IHostEnvironment? _environment;

    /// <param name="environment">Relaxes the credentials rule in Development; without one, the rule applies.</param>
    public CorsAllowedOriginsValidator(IHostEnvironment? environment = null) => _environment = environment;

    public ValidateOptionsResult Validate(string? name, ApiPipelineCorsOptions options)
    {
        if (!options.Enabled || options.AllowedOrigins is not { Count: > 0 } origins)
        {
            return ValidateOptionsResult.Success;
        }

        var problems = new List<string>();
        if (origins.Count > 1 && origins.Contains("*", StringComparer.Ordinal))
        {
            problems.Add(
                $"{Key} lists '*' with other origins. '*' allows every origin only as the only entry; listed " +
                "with others it is compared as a literal origin and matches nothing. Use [\"*\"] alone to allow " +
                "every origin, or remove it to allow only the listed ones.");
        }

        var strict = _environment is null || StrictEnvironment.Applies(_environment);
        foreach (var origin in origins)
        {
            if (Problem(origin) is { } problem)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Key} entry '{origin}' {problem}"));
            }
            else if (options.AllowCredentials && strict && IsPlainHttp(origin))
            {
                problems.Add(
                    $"{Key} entry '{origin}' is plain HTTP while AllowCredentials is true. Anyone on the network " +
                    "path to that origin's users can inject script into its pages and call this API with their " +
                    "credentials. Serve the front end over https:// and list that origin. (Allowed in Development only" +
                    (_environment is null ? ".)" : $"; this is {StrictEnvironment.Describe(_environment)}.)"));
            }
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private static bool IsPlainHttp(string origin) =>
        origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="origin"/> can never match; <c>null</c> when well-formed.</summary>
    private static string? Problem(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return "is empty. Remove it.";
        }

        if (origin == "*")
        {
            return null;
        }

        if (string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return "is the 'null' origin, which browsers send for sandboxed documents and local files. " +
                   "Allowing it lets any of them call the API. Remove it.";
        }

        if (origin.Contains('*', StringComparison.Ordinal))
        {
            return "is a wildcard pattern, which is not supported and never matches. List each origin, " +
                   "or use \"*\" alone to allow every origin.";
        }

        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return "is not an origin. Use the form scheme://host[:port], for example 'https://app.example.com'.";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "contains user information, which a browser never sends in an Origin header. " +
                   "Use the form scheme://host[:port].";
        }

        var port = uri.IsDefaultPort ? string.Empty : string.Create(CultureInfo.InvariantCulture, $":{uri.Port}");
        var unicode = $"{uri.Scheme}://{uri.Host}{port}";
        var ascii = $"{uri.Scheme}://{uri.IdnHost}{port}";

        // The framework converts an IDN host to the punycode the browser sends.
        if (string.Equals(origin, unicode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin, ascii, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"never matches: a browser's Origin header is scheme://host[:port] with no path, query, " +
               $"trailing slash, surrounding spaces, or default port. Use '{unicode}'.";
    }
}
