using System.Text.Json;
using System.Text.RegularExpressions;

namespace MattsRecords.Services;

public sealed partial class DiscogsLookup(HttpClient client, ILogger<DiscogsLookup> logger)
{
    public async Task<IReadOnlyList<PressingCandidate>> FindBarcodeAsync(string term, CancellationToken cancellationToken)
    {
        var codes = RecordBarcode.EquivalentCodes(term);
        if (codes.Length == 0)
            return [];
        using var json = await ReadAsync($"database/search?barcode={codes[0]}&type=release&format=Vinyl", cancellationToken);
        if (json is null)
            return [];

        var candidates = new List<PressingCandidate>();
        foreach (var result in List(json.RootElement, "results"))
        {
            var formats = Strings(result, "format");
            var barcode = MatchBarcode(Strings(result, "barcode"), codes);
            var creditAndTitle = Text(result, "title").Split(" - ", 2);
            if (Number(result, "id") is not { } id || barcode is null ||
                creditAndTitle.Length != 2 || !formats.Contains("Vinyl"))
                continue;
            candidates.Add(new PressingCandidate(creditAndTitle[1], CleanName(creditAndTitle[0]), Text(result, "catno"),
                CleanName(Strings(result, "label").FirstOrDefault() ?? ""), barcode, Text(result, "country"),
                Number(result, "year"), Format(formats), Kind(formats), null, null, null,
                PrimaryGenre(result), Edition(formats), false, id));
        }
        return candidates.DistinctBy(candidate => candidate.DiscogsId).Take(12).ToArray();
    }

    public async Task<PressingCandidate?> GetDetailsAsync(int id, string term, CancellationToken cancellationToken)
    {
        using var json = await ReadAsync($"releases/{id}", cancellationToken);
        if (json is null)
            return null;
        var release = json.RootElement;
        var formats = List(release, "formats").Where(format => Text(format, "name") == "Vinyl").ToArray();
        var barcode = MatchBarcode(List(release, "identifiers").Where(item => Text(item, "type") == "Barcode")
            .Select(item => Text(item, "value")), RecordBarcode.EquivalentCodes(term));
        if (barcode is null || formats.Length == 0)
            return null;

        var descriptions = formats.SelectMany(format => Strings(format, "descriptions")).Distinct().ToArray();
        var label = List(release, "labels").FirstOrDefault();
        var artist = string.Join(" ", List(release, "artists").SelectMany(item => new[]
            { CleanName(Text(item, "name")), Text(item, "join") }).Where(part => part.Length > 0));
        int? releaseYear = null;
        if (Number(release, "master_id") is { } master)
        {
            try
            {
                using var masterData = await ReadAsync($"masters/{master}", cancellationToken);
                if (masterData is not null)
                    releaseYear = Number(masterData.RootElement, "year");
            }
            catch (HttpRequestException) { /* Keep the verified pressing year or other details. */ }
        }
        return new PressingCandidate(Text(release, "title"), artist, Text(label, "catno"), CleanName(Text(label, "name")),
            barcode, Text(release, "country"), Number(release, "year"), Format(descriptions), Kind(descriptions),
            null, null, releaseYear, PrimaryGenre(release), Edition(descriptions), true, id);
    }

    private async Task<JsonDocument?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(path, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Discogs timed out.", exception);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Discogs returned an invalid response.");
            throw new HttpRequestException("Discogs returned an invalid response.", exception);
        }
    }

    private static string? MatchBarcode(IEnumerable<string> values, string[] codes) =>
        values.SelectMany(RecordBarcode.EquivalentCodes).FirstOrDefault(codes.Contains);

    private static string Kind(string[] descriptions) => descriptions.Contains("Compilation") ? "Compilation"
        : descriptions.Contains("Single") ? "Single" : descriptions.Contains("EP") ? "EP"
        : descriptions.Contains("Album") || descriptions.Contains("LP") ? "Album" : "";

    private static string Format(string[] descriptions) => descriptions.Contains("7\"") ? "7-inch vinyl"
        : descriptions.Contains("10\"") ? "10-inch vinyl"
        : descriptions.Contains("12\"") || descriptions.Contains("LP") ? "12-inch vinyl" : "Vinyl";

    private static string Edition(string[] descriptions) => string.Join(" / ", descriptions.Where(description =>
        description is "Reissue" or "Remastered" or "Limited Edition" or "Promo" or "Picture Disc"));

    private static string PrimaryGenre(JsonElement item) =>
        Strings(item, "style").Concat(Strings(item, "styles")).Concat(Strings(item, "genre"))
            .Concat(Strings(item, "genres")).FirstOrDefault() ?? "";

    private static string CleanName(string name) => DiscogsSuffix().Replace(name, "");

    [GeneratedRegex(@" \(\d+\)$")]
    private static partial Regex DiscogsSuffix();

    private static JsonElement Property(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    private static string Text(JsonElement item, string property) =>
        Property(item, property) is { ValueKind: JsonValueKind.String } text ? text.GetString()! : "";

    private static int? Number(JsonElement item, string property)
    {
        var value = Property(item, property);
        var number = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed) ? parsed : 0;
        return number > 0 ? number : null;
    }

    private static IEnumerable<JsonElement> List(JsonElement item, string property) =>
        Property(item, property) is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray() : [];

    private static string[] Strings(JsonElement item, string property) =>
        List(item, property).Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray();
}
