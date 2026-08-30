using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace ApiPipeline.NET.Shared;

/// <summary>
/// Rejects enum options bound from a number with no member (<c>"3"</c>), which would silently take
/// a switch's default arm. <c>[EnumDataType]</c> is not trimming-safe.
/// </summary>
internal static class EnumValue
{
    /// <summary>A failure when <paramref name="value"/> is no member of <typeparamref name="T"/>; otherwise null.</summary>
    public static ValidationResult? Check<T>(T value, string memberName, string prefix = "")
        where T : struct, Enum =>
        Enum.IsDefined(value)
            ? null
            : new ValidationResult(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{prefix}{memberName} ({value:D}) is not a {typeof(T).Name} value. Use one of: {string.Join(", ", Enum.GetNames<T>())}."),
                [memberName]);
}
