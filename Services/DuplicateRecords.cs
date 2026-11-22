using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MattsRecords.Models;

namespace MattsRecords.Services;

public sealed record RecordMatch(int Id, string Title, string Artist, string Reason, string? CatalogueNumber,
    string? Country, int? PressingYear, string? Format);

public sealed record DuplicateReview(string Key, IReadOnlyList<RecordMatch> Matches);

public sealed record RecordSave(RecordChange Change, DuplicateReview? Review = null, bool UpdatedExisting = false);

internal sealed record RecordIdentity(string Title, string Artist, string? Barcode, Guid? MusicBrainzId, int? DiscogsId)
{
    private const CompareOptions Comparison =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreSymbols;

    public (Album Record, RecordMatch Match)[] FindMatches(IEnumerable<Album> collection, bool matchTitleAlone = false)
    {
        var codes = RecordBarcode.EquivalentCodes(Barcode ?? "");
        var artist = ComparableArtist(Artist);
        var matches = new List<(Album, RecordMatch)>();
        foreach (var record in collection)
        {
            var credit = ComparableArtist(record.DisplayArtist);
            var samePressing = (MusicBrainzId.HasValue && record.MusicBrainzId == MusicBrainzId)
                || (DiscogsId.HasValue && record.DiscogsId == DiscogsId);
            var sameBarcode = codes.Length > 0 && RecordBarcode.EquivalentCodes(record.Barcode ?? "").Any(codes.Contains);
            var sameAlbum = Title.Length > 0 && Equal(Title, record.Name) && (matchTitleAlone || Equal(artist, credit));
            if (!samePressing && !sameBarcode && !sameAlbum)
                continue;

            var reason = samePressing ? "Same pressing" : sameBarcode ? "Same barcode"
                : matchTitleAlone ? "Same title" : "Same title and artist";
            matches.Add((record, new RecordMatch(record.Id, record.Name, record.DisplayArtist, reason,
                record.CatalogNumber, record.Country, record.PressingYear, record.Format)));
        }
        return matches.ToArray();
    }

    public DuplicateReview Review((Album Record, RecordMatch Match)[] matches)
    {
        var state = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Identity = this,
            Records = matches.OrderBy(item => item.Record.Id)
                .Select(item => new { item.Record.Id, item.Record.RowVersion })
        });
        return new DuplicateReview(Convert.ToHexString(SHA256.HashData(state)), matches.Select(item => item.Match).ToArray());
    }

    private static bool Equal(string left, string right)
    {
        var options = left.Any(char.IsLetterOrDigit) && right.Any(char.IsLetterOrDigit)
            ? Comparison : Comparison & ~CompareOptions.IgnoreSymbols;
        return CultureInfo.InvariantCulture.CompareInfo.Compare(left, right, options) == 0;
    }

    private static string ComparableArtist(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Equal(name, "Various Artists") || Equal(name, "Uncredited"))
            return "";
        return name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
    }
}
