using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Configuration;

/// <summary>
/// Binds <c>"Key": []</c> as an empty list. The binder skips it, leaving <c>null</c> ("not
/// configured"), where the docs promise an empty list replaces the default.
/// </summary>
internal static class EmptyListBinding
{
    /// <summary>Applies <paramref name="setEmpty"/> when <paramref name="key"/> is an empty list.</summary>
    public static OptionsBuilder<TOptions> BindEmptyList<TOptions>(
        this OptionsBuilder<TOptions> options,
        IConfiguration section,
        string key,
        Action<TOptions> setEmpty)
        where TOptions : class =>
        options.Configure(o =>
        {
            if (IsEmptyList(section, key))
            {
                setEmpty(o);
            }
        });

    /// <summary>Whether <paramref name="key"/> is present, blank and without entries.</summary>
    internal static bool IsEmptyList(IConfiguration section, string key)
    {
        var list = section.GetSection(key);
        return list.Value is { } value && string.IsNullOrWhiteSpace(value) && !list.GetChildren().Any();
    }
}
