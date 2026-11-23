namespace MattsRecords.Models;

public enum RecordMedium
{
    Vinyl = 0,
    CD = 1,
    Cassette = 2
}

public static class RecordMedia
{
    public static RecordMedium? FromFormat(string? format)
    {
        var value = format?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Contains("vinyl", StringComparison.OrdinalIgnoreCase) || value.Equals("LP", StringComparison.OrdinalIgnoreCase))
            return RecordMedium.Vinyl;
        if (value.Contains("cassette", StringComparison.OrdinalIgnoreCase))
            return RecordMedium.Cassette;
        if (value.Equals("Compact Disc", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SACD", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("HDCD", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("CDR", StringComparison.OrdinalIgnoreCase) ||
            value.Split(' ', '-', '+', ',', '/', '(').Contains("CD", StringComparer.OrdinalIgnoreCase))
            return RecordMedium.CD;
        return null;
    }

    public static RecordMedium? FromFormats(IEnumerable<string> formats)
    {
        var media = formats.Select(FromFormat).OfType<RecordMedium>().Distinct().Take(2).ToArray();
        return media is [var medium] ? medium : null;
    }

    public static bool HasDetails(RecordMedium medium, string? format) => !string.IsNullOrWhiteSpace(format) &&
        !string.Equals(format.Trim(), medium.ToString(), StringComparison.OrdinalIgnoreCase);
}
