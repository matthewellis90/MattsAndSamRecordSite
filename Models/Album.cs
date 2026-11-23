namespace MattsRecords.Models;

public enum RecordKind
{
    Album,
    Single,
    EP,
    Compilation
}

public sealed class Album
{
    public const string VinylFormat = "Vinyl";

    public int Id { get; set; }
    public DateTime? AddedAtUtc { get; set; }
    public string Name { get; set; } = "";
    public int? ArtistId { get; set; }
    public Artist? Artist { get; set; }
    public string? ArtistName { get; set; }
    public string DisplayArtist => Artist?.Name
        ?? (string.IsNullOrWhiteSpace(ArtistName) ? "Uncredited" : ArtistName);
    public int? Year { get; set; }
    public RecordKind Kind { get; set; }
    public RecordMedium Medium { get; set; }
    public string? Format { get; set; }
    public bool HasVinylFormatDetails => Medium == RecordMedium.Vinyl && IsDetailedVinylFormat(Format);

    public static bool IsDetailedVinylFormat(string? format) => !string.IsNullOrWhiteSpace(format)
        && !string.Equals(format.Trim(), VinylFormat, StringComparison.OrdinalIgnoreCase);
    public string? Notes { get; set; }
    public string? CoverUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string? Label { get; set; }
    public string? Genre { get; set; }
    public string? Country { get; set; }
    public string? CatalogNumber { get; set; }
    public string? Barcode { get; set; }
    public int? PressingYear { get; set; }
    public Guid? MusicBrainzId { get; set; }
    public int? DiscogsId { get; set; }
    public Guid? FrontSleeveKey { get; set; }
    public Guid? BackSleeveKey { get; set; }
    public bool PressingReviewed { get; set; }
    public bool PressingEstimated { get; set; }
    public bool HasCover => FrontSleeveKey.HasValue || CatalogValidation.IsCoverUrl(CoverUrl);
    public byte[] RowVersion { get; set; } = [];
}
