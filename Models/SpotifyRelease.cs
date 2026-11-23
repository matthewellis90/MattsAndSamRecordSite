namespace MattsRecords.Models;

public static class SpotifyRelease
{
    private const string Origin = "https://open.spotify.com";

    public static string? Normalize(string? value) =>
        GetPath(value) is { } path ? Origin + path : null;

    public static string? Embed(string? value) =>
        GetPath(value) is { } path ? Origin + "/embed" + path : null;

    public static string Search(Album record)
    {
        var query = record.DisplayArtist == "Uncredited" ? record.Name : $"{record.DisplayArtist} {record.Name}";
        return Origin + "/search/" + Uri.EscapeDataString(query);
    }

    private static string? GetPath(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "open.spotify.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 3 && segments[0].StartsWith("intl-", StringComparison.Ordinal))
            segments = segments[1..];
        return segments is [("album" or "track"), { Length: 22 } id] && id.All(char.IsAsciiLetterOrDigit)
            ? "/" + string.Join('/', segments) : null;
    }
}
