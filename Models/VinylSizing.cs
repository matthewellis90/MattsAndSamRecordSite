using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace MattsRecords.Models;

public enum VinylSize
{
    [Display(Name = "Keep current format")] KeepCurrent,
    [Display(Name = "Size unknown")] Unknown,
    [Display(Name = "7-inch")] SevenInch,
    [Display(Name = "10-inch")] TenInch,
    [Display(Name = "12-inch")] TwelveInch
}

public static partial class VinylSizing
{
    public static VinylSize FromFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        null or "" or "vinyl" => VinylSize.Unknown,
        "7-inch vinyl" => VinylSize.SevenInch,
        "10-inch vinyl" => VinylSize.TenInch,
        "12-inch vinyl" => VinylSize.TwelveInch,
        _ => VinylSize.KeepCurrent
    };

    public static string? Apply(VinylSize size, string? currentFormat) => size switch
    {
        VinylSize.Unknown => Album.VinylFormat,
        VinylSize.SevenInch => "7-inch vinyl",
        VinylSize.TenInch => "10-inch vinyl",
        VinylSize.TwelveInch => "12-inch vinyl",
        _ => currentFormat
    };

    public static bool HasSize(string? format) => !string.IsNullOrWhiteSpace(format) && SizePattern().IsMatch(format);

    [GeneratedRegex("""(?<!\d)\d{1,2}\s*(?:[-–]\s*)?(?:inch(?:es)?\b|in\b|["″])""", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();
}
