using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Shared;

/// <summary>A <see cref="TimeSpan"/> setting and its unsupported whole-number key, if any.</summary>
/// <param name="Name">The key, e.g. <c>Window</c>.</param>
/// <param name="FormerName">The unsupported whole-number key, e.g. <c>WindowSeconds</c>; <c>null</c> if none.</param>
/// <param name="Collection">For a setting of each list element, the list's key.</param>
internal readonly record struct DurationKey(string Name, string? FormerName, string? Collection = null);

/// <summary>
/// Fails startup on two silent duration mistakes: an unsupported whole-number key (not read, so the
/// default applies) and a bare number (read as days: <c>"Window": 60</c> is 60 days).
/// </summary>
internal static class DurationSettings
{
    /// <summary>Adds the check for <paramref name="section"/>; call once, where it is bound.</summary>
    public static OptionsBuilder<TOptions> ValidateDurations<TOptions>(
        this OptionsBuilder<TOptions> options,
        IConfigurationSection section,
        params DurationKey[] keys)
        where TOptions : class
    {
        options.Services.AddSingleton<IValidateOptions<TOptions>>(new Validator<TOptions>(options.Name, section, keys));
        return options;
    }

    internal static string RenamedMessage(string path, string name) =>
        $"'{path}' is not read: the setting is '{name}', which takes a duration instead of a number, " +
        "for example \"00:00:30\" for 30 seconds. Rename the key and convert its value.";

    internal static string BareNumberMessage(string path, string value, long number) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"'{path}' is '{value}', which reads as {number} days. Write a duration as [d.]hh:mm:ss[.fff]: " +
            $"\"{TimeSpan.FromSeconds(number):c}\" for {number} seconds, or \"{number}.00:00:00\" if you mean days.");

    private sealed class Validator<TOptions>(string? name, IConfigurationSection section, DurationKey[] keys)
        : IValidateOptions<TOptions>
        where TOptions : class
    {
        public ValidateOptionsResult Validate(string? optionsName, TOptions options)
        {
            var defaultName = Options.DefaultName;
            if (!string.Equals(optionsName ?? defaultName, name ?? defaultName, StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Skip;
            }

            List<string>? failures = null;
            foreach (var key in keys)
            {
                IEnumerable<IConfigurationSection> scopes = key.Collection is null
                    ? [section]
                    : section.GetSection(key.Collection).GetChildren();

                foreach (var scope in scopes)
                {
                    if (key.FormerName is not null && scope.GetSection(key.FormerName) is { Value: not null } former)
                    {
                        (failures ??= []).Add(RenamedMessage(former.Path, key.Name));
                    }

                    var current = scope.GetSection(key.Name);
                    if (current.Value?.Trim() is { Length: > 0 } raw
                        && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        (failures ??= []).Add(BareNumberMessage(current.Path, raw, number));
                    }
                }
            }

            return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}

/// <summary>Range checks for <see cref="TimeSpan"/> options; <c>[Range(typeof(TimeSpan))]</c> is not trimming-safe.</summary>
internal static class DurationRange
{
    /// <summary>A failure when <paramref name="value"/> is set and outside [min, max].</summary>
    public static ValidationResult? Check(TimeSpan? value, TimeSpan min, TimeSpan max, string memberName, string prefix = "") =>
        value is { } duration && (duration < min || duration > max)
            ? new ValidationResult(
                string.Create(CultureInfo.InvariantCulture, $"{prefix}{memberName} ({duration:c}) must be between {min:c} and {max:c}."),
                [memberName])
            : null;
}
