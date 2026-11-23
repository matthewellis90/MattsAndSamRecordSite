using System.Globalization;
using MattsRecords.Models;

namespace MattsRecords.Services;

public sealed record CatalogFacets(int[] Decades, string[] Formats, string[] Genres, string[] Labels);

internal static class CatalogSearch
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    public static string[] Terms(string? search) => string.IsNullOrWhiteSpace(search)
        ? [] : search[..Math.Min(search.Length, 200)].Split(Separators, StringSplitOptions.RemoveEmptyEntries).Take(8).ToArray();

    public static bool Matches(Album record, string[] terms)
    {
        var words = string.Join(' ', record.Name, record.DisplayArtist, record.Label, record.Genre, record.Format, record.Medium,
            record.Year, record.PressingYear, record.CatalogNumber, record.Barcode)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term => words.Any(word => MatchesWord(word, term)));
    }

    private static bool MatchesWord(string word, string term)
    {
        if (CultureInfo.InvariantCulture.CompareInfo.IndexOf(word, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            return true;

        if (term.Length < 4 || !term.All(char.IsLetter) || Math.Abs(word.Length - term.Length) > 1)
            return false;

        word = word.ToUpperInvariant();
        term = term.ToUpperInvariant();
        if (word.Length < term.Length)
            (word, term) = (term, word);

        var start = 0;
        while (start < term.Length && word[start] == term[start])
            start++;

        if (word.Length > term.Length)
            return word.AsSpan(start + 1).SequenceEqual(term.AsSpan(start));

        if (start == term.Length)
            return true;

        return word.AsSpan(start + 1).SequenceEqual(term.AsSpan(start + 1)) ||
            (start + 1 < term.Length && word[start] == term[start + 1] &&
                word[start + 1] == term[start] && word.AsSpan(start + 2).SequenceEqual(term.AsSpan(start + 2)));
    }
}
