using System.Text.Json;
using MattsRecords.Models;

namespace MattsRecords.Services;

public sealed record PressingCandidate(string Title, string Artist, string CatalogueNumber, string Label,
    string Barcode, string Country, int? Year, string Format, string Kind, Guid? Id, string? CoverUrl,
    int? ReleaseYear, string Genre, string Edition, bool IsDetailed, int? DiscogsId = null,
    int? ExistingRecordId = null)
{
    public IReadOnlyList<RecordMatch> CollectionMatches { get; init; } = [];
    public string? SpotifyUrl { get; init; }
    public int? LinkedDiscogsId { get; init; }
    public RecordMedium? Medium { get; init; }
}

public sealed class PressingLookup(HttpClient client)
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static DateTimeOffset nextRequest;

    public async Task<IReadOnlyList<PressingCandidate>> FindAsync(string field, string term, string? artist,
        CancellationToken cancellationToken, bool requireComplete = false, bool broadVinylSearch = false)
    {
        var identifier = term.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? term;
        if (field != "Barcode" && Guid.TryParse(identifier, out var pressingId))
            return await GetDetailsAsync(pressingId, cancellationToken) is { } release ? [release] : [];

        var barcodes = field == "Barcode" ? RecordBarcode.EquivalentCodes(term) : [];
        if (field == "Barcode" && barcodes.Length == 0)
            return [];

        var query = field switch
        {
            "Barcode" => "(" + string.Join(" OR ", barcodes.Select(code => "barcode:" + Quoted(code))) + ")",
            "Catalogue" => "catno:" + Quoted(term),
            _ => "release:" + Quoted(term)
        };
        if (!string.IsNullOrWhiteSpace(artist))
            query += " AND artist:" + Quoted(artist);
        if (broadVinylSearch)
            query += " AND format:vinyl";

        using var json = await ReadMusicBrainzAsync(
            $"release?query={Uri.EscapeDataString(query)}&limit={(requireComplete || broadVinylSearch ? 100 : field == "Barcode" ? 50 : 12)}&fmt=json",
            cancellationToken);
        if (json is null)
            return [];

        var releases = Array(json.RootElement, "releases").ToArray();
        if (requireComplete && (!json.RootElement.TryGetProperty("count", out var count) ||
            !count.TryGetInt32(out var total) || total > releases.Length))
            return [];
        var candidates = releases.Select(Parse).OfType<PressingCandidate>()
            .Where(candidate => candidate.Title.Length > 0);
        if (field == "Barcode")
            candidates = candidates.Where(candidate => barcodes.Contains(candidate.Barcode))
                .OrderByDescending(candidate => candidate.Medium.HasValue);
        return (requireComplete || broadVinylSearch ? candidates : candidates.Take(12)).ToArray();
    }

    public async Task<PressingCandidate?> GetDetailsAsync(Guid id, CancellationToken cancellationToken)
    {
        using var json = await ReadMusicBrainzAsync($"release/{id}?inc=artist-credits+labels+release-groups+media+genres+url-rels&fmt=json",
            cancellationToken);
        if (json is null || Parse(json.RootElement) is not { } candidate)
            return null;

        if (json.RootElement.TryGetProperty("release-group", out var group) &&
            Guid.TryParse(Text(group, "id"), out var groupId))
        {
            if (candidate.ReleaseYear is null || candidate.Genre.Length == 0)
            {
                try
                {
                    using var groupInfo = await ReadMusicBrainzAsync($"release-group/{groupId}?inc=genres&fmt=json", cancellationToken);
                    if (groupInfo is not null)
                        candidate = candidate with
                        {
                            ReleaseYear = candidate.ReleaseYear ?? ReadYear(Text(groupInfo.RootElement, "first-release-date")),
                            Genre = candidate.Genre.Length == 0 ? ReadGenre(groupInfo.RootElement) : candidate.Genre
                        };
                }
                catch (HttpRequestException) { /* Pressing metadata is still available. */ }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }

            if (candidate.CoverUrl is null && await GroupHasFrontAsync(groupId, cancellationToken))
                candidate = candidate with { CoverUrl = $"https://coverartarchive.org/release-group/{groupId}/front-500" };
        }
        var urls = Array(json.RootElement, "relations")
            .Where(relation => relation.ValueKind == JsonValueKind.Object && relation.TryGetProperty("url", out _))
            .Select(relation => Text(relation.GetProperty("url"), "resource")).ToArray();
        var spotify = urls.Select(SpotifyRelease.Normalize).OfType<string>().Distinct().Take(2).ToArray();
        var discogs = urls.Select(DiscogsLookup.ReleaseId).OfType<int>().Distinct().Take(2).ToArray();
        return candidate with
        {
            IsDetailed = true,
            SpotifyUrl = spotify.Length == 1 ? spotify[0] : null,
            LinkedDiscogsId = discogs.Length == 1 ? discogs[0] : null
        };
    }

    public async Task<string?> FindAlbumCoverAsync(string title, string artist, CancellationToken cancellationToken)
    {
        var query = $"releasegroup:{Quoted(title)} AND artist:{Quoted(artist)}";
        using var json = await ReadMusicBrainzAsync(
            $"release-group?query={Uri.EscapeDataString(query)}&limit=8&fmt=json", cancellationToken);
        if (json is null)
            return null;

        foreach (var group in Array(json.RootElement, "release-groups"))
        {
            var credit = string.Concat(Array(group, "artist-credit").Select(item => Text(item, "name") + Text(item, "joinphrase")));
            if (!string.Equals(title, Text(group, "title"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(artist, credit, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(Text(group, "id"), out var groupId))
                continue;
            return await GroupHasFrontAsync(groupId, cancellationToken)
                ? $"https://coverartarchive.org/release-group/{groupId}/front-500" : null;
        }
        return null;
    }

    private async Task<bool> GroupHasFrontAsync(Guid groupId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await client.GetAsync($"https://coverartarchive.org/release-group/{groupId}", timeout.Token);
            if (!response.IsSuccessStatusCode)
                return false;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token),
                cancellationToken: timeout.Token);
            return Array(json.RootElement, "images").Any(image => image.TryGetProperty("front", out var front) &&
                front.ValueKind == JsonValueKind.True);
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task<JsonDocument?> ReadMusicBrainzAsync(string path, CancellationToken cancellationToken)
    {
        var cooldown = TimeSpan.FromMilliseconds(1150);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var remaining = nextRequest - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);
            using var response = await client.GetAsync(path, cancellationToken);
            var retry = response.Headers.RetryAfter;
            var requestedCooldown = retry?.Delta ?? retry?.Date - DateTimeOffset.UtcNow;
            if (requestedCooldown > cooldown)
                cooldown = requestedCooldown.Value;
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            try
            {
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new HttpRequestException("MusicBrainz returned an invalid response.", exception);
            }
        }
        finally
        {
            nextRequest = DateTimeOffset.UtcNow + cooldown;
            Gate.Release();
        }
    }

    private static PressingCandidate? Parse(JsonElement release)
    {
        if (!Guid.TryParse(Text(release, "id"), out var id))
            return null;

        var credits = string.Concat(Array(release, "artist-credit").Select(credit => Text(credit, "name") + Text(credit, "joinphrase")));
        var labelInfo = Array(release, "label-info").FirstOrDefault();
        var label = labelInfo.ValueKind == JsonValueKind.Object && labelInfo.TryGetProperty("label", out var definition)
            ? Text(definition, "name") : "";
        var year = ReadYear(Text(release, "date"));
        var primary = "";
        if (release.TryGetProperty("release-group", out var group))
            primary = Array(group, "secondary-types").Any(type => type.GetString() == "Compilation")
                ? "Compilation" : Text(group, "primary-type");
        var cover = release.TryGetProperty("cover-art-archive", out var art) &&
            art.TryGetProperty("front", out var hasFront) && hasFront.ValueKind == JsonValueKind.True
            ? $"https://coverartarchive.org/release/{id}/front-500" : null;

        var genre = ReadGenre(release);
        var formats = Array(release, "media").Select(medium => VinylSize(Text(medium, "format")))
            .Where(format => format.Length > 0).ToArray();
        var medium = RecordMedia.FromFormats(formats);
        if (formats.Length > 0 && !medium.HasValue)
            return null;
        var displayFormats = formats.Distinct().ToArray();
        var format = formats.Length > 1 && displayFormats.Length == 1 && medium is RecordMedium.CD or RecordMedium.Cassette
            ? $"{formats.Length} × {displayFormats[0]}" : string.Join(" / ", displayFormats);
        return new PressingCandidate(Text(release, "title"), credits, Text(labelInfo, "catalog-number"), label,
            Text(release, "barcode"), Text(release, "country"), year, format,
            primary, id, cover, ReadYear(Text(group, "first-release-date")),
            genre.Length == 0 ? ReadGenre(group) : genre, Text(release, "disambiguation"), false) { Medium = medium };
    }

    private static int? ReadYear(string date) =>
        date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var year) && year >= 1000 ? year : null;

    private static string VinylSize(string format) => format switch
    {
        "7\" Vinyl" => "7-inch vinyl",
        "10\" Vinyl" => "10-inch vinyl",
        "12\" Vinyl" => "12-inch vinyl",
        _ => format
    };

    private static string ReadGenre(JsonElement element) => Text(Array(element, "genres")
        .OrderByDescending(genre => genre.TryGetProperty("count", out var count) && count.TryGetInt32(out var votes) ? votes : 0)
        .FirstOrDefault(), "name");

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static string Quoted(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
