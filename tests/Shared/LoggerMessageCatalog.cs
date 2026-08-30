using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ApiPipeline.NET.TestSupport;

/// <summary>
/// Reads a package's <c>[LoggerMessage]</c> definitions and the event table in
/// <c>docs/OBSERVABILITY.md</c>. Operators alert and query on event IDs (the docs say they are
/// stable), so tests pin each one: renumbering an event, or changing its level, must fail a test
/// rather than silently break an alert. Linked into each test project from <c>tests/Shared</c>.
/// </summary>
internal static partial class LoggerMessageCatalog
{
    /// <summary>Every <c>[LoggerMessage]</c> method of <paramref name="logTypeName"/>: name to (event ID, level).</summary>
    public static Dictionary<string, (int Id, LogLevel Level)> Of(Assembly assembly, string logTypeName)
    {
        var type = assembly.GetType(logTypeName, throwOnError: true)!;
        return type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => (method, attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(pair => pair.attribute is not null)
            .ToDictionary(pair => pair.method.Name, pair => (pair.attribute!.EventId, pair.attribute.Level));
    }

    /// <summary>The event table of <c>docs/OBSERVABILITY.md</c>: event ID to the level it documents.</summary>
    public static Dictionary<int, string> DocumentedEvents()
    {
        var documented = new Dictionary<int, string>();
        var inTable = false;
        foreach (var line in File.ReadLines(RepositoryFile("docs/OBSERVABILITY.md")))
        {
            if (line.StartsWith("| Event ID |", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable)
            {
                continue;
            }

            if (!line.StartsWith('|'))
            {
                break;
            }

            if (Row().Match(line) is { Success: true } row)
            {
                foreach (var id in row.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    documented[int.Parse(id, System.Globalization.CultureInfo.InvariantCulture)] = row.Groups["level"].Value;
                }
            }
        }

        return documented;
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiPipeline.NET.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root (ApiPipeline.NET.slnx) not found."),
            relativePath);
    }

    [GeneratedRegex(@"^\|\s*(?<ids>[0-9][0-9, ]*)\s*\|\s*(?<level>[A-Za-z]+)\s*\|")]
    private static partial Regex Row();
}
