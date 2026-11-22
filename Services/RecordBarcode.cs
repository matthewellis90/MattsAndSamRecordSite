namespace MattsRecords.Services;

internal static class RecordBarcode
{
    public static string[] EquivalentCodes(string text)
    {
        var digits = string.Concat(text.Where(character => character != '-' && !char.IsWhiteSpace(character)));
        if (!IsValid(digits))
            return [];

        // MusicBrainz may store either a UPC-A or its zero-prefixed EAN/GTIN.
        var canonical = digits.PadLeft(14, '0');
        return new[] { digits.Length, 12, 13, 14 }.Distinct()
            .Where(length => canonical.AsSpan(0, 14 - length).IndexOfAnyExcept('0') < 0)
            .Select(length => canonical[^length..]).ToArray();
    }

    private static bool IsValid(string digits)
    {
        if (digits.Length is not (8 or 12 or 13 or 14) || digits.Any(character => !char.IsAsciiDigit(character)))
            return false;

        var sum = 0;
        for (var position = 0; position < digits.Length; position++)
            sum += (digits[^(position + 1)] - '0') * (position % 2 == 0 ? 1 : 3);
        return sum % 10 == 0 && digits.Any(character => character != '0');
    }
}
