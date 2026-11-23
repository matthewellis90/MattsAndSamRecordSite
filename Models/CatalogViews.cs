using System.ComponentModel.DataAnnotations;
using MattsRecords.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MattsRecords.Models;

public sealed record CatalogView(RecordQuery Query, IReadOnlyList<CatalogSection> Sections, RecordMedium? PagedSection, IReadOnlyList<ArtistSummary> Artists,
    CatalogFacets Facets, IReadOnlyList<Album> Recent, Guid? ShareToken = null);

public sealed record CatalogSection(RecordMedium Medium, RecordPage Results);

public sealed record ArtistsView(string? Search, IReadOnlyList<ArtistSummary> Artists);

public sealed record DeleteForm(int Id, string Name, string Version, string Entity);

public sealed class ArtistForm : IValidatableObject
{
    public int? Id { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        CatalogValidation.ValidateVersion(Id, Version);
}

public sealed class RecordForm : IValidatableObject
{
    public int? Id { get; set; }

    [Required, StringLength(200), Display(Name = "Title")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Artist")]
    public int? ArtistId { get; set; }
    [Display(Name = "New artist name"), StringLength(200)]
    public string? NewArtistName { get; set; }

    [Display(Name = "Release year"), Range(0, 9999)]
    public int? Year { get; set; }

    [EnumDataType(typeof(RecordKind)), Display(Name = "Release type")]
    public RecordKind Kind { get; set; } = RecordKind.Album;

    [EnumDataType(typeof(RecordMedium)), Display(Name = "Media")]
    public RecordMedium Medium { get; set; } = RecordMedium.Vinyl;

    [StringLength(100), Display(Name = "Format details")]
    public string? Format { get; set; }

    [StringLength(2000)]
    public string? Notes { get; set; }

    [Display(Name = "Cover image URL"), StringLength(2048)]
    public string? CoverUrl { get; set; }

    [Display(Name = "Spotify album or track URL"), StringLength(500)]
    public string? SpotifyUrl { get; set; }

    [StringLength(100), Display(Name = "Label")]
    public string? Label { get; set; }

    [StringLength(80), Display(Name = "Genre")]
    public string? Genre { get; set; }

    [StringLength(80), Display(Name = "Pressing country")]
    public string? Country { get; set; }

    [StringLength(100), Display(Name = "Catalogue number")]
    public string? CatalogNumber { get; set; }

    [StringLength(32), Display(Name = "Barcode")]
    public string? Barcode { get; set; }

    [Range(1000, 9999), Display(Name = "Pressing year")]
    public int? PressingYear { get; set; }

    [Display(Name = "MusicBrainz release ID")]
    public Guid? MusicBrainzId { get; set; }
    [Range(1, int.MaxValue), Display(Name = "Discogs release ID")]
    public int? DiscogsId { get; set; }

    public string? Version { get; set; }

    public bool ConfirmPressingReviewed { get; set; }

    [BindNever]
    public IReadOnlyList<ArtistSummary> Artists { get; set; } = [];

    [StringLength(64)]
    public string? DuplicateReviewKey { get; set; }

    [StringLength(24)]
    public string? DuplicateAction { get; set; }

    [BindNever]
    public DuplicateReview? Review { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var error in CatalogValidation.ValidateVersion(Id, Version))
        {
            yield return error;
        }
        if (!string.IsNullOrWhiteSpace(CoverUrl) && !CatalogValidation.IsCoverUrl(CoverUrl.Trim()))
        {
            yield return new ValidationResult("Enter an HTTPS image URL.", [nameof(CoverUrl)]);
        }
        if (!string.IsNullOrWhiteSpace(NewArtistName) && (Id.HasValue || ArtistId.HasValue))
            yield return new ValidationResult("Choose an existing artist or enter a new artist name.", [nameof(NewArtistName)]);
        if (!string.IsNullOrWhiteSpace(SpotifyUrl) && SpotifyRelease.Normalize(SpotifyUrl) is null)
            yield return new ValidationResult("Enter an HTTPS Spotify album or track link.", [nameof(SpotifyUrl)]);
        if (RecordMedia.FromFormat(Format) is RecordMedium specified && specified != Medium)
            yield return new ValidationResult("Enter format details for the selected media.", [nameof(Format)]);
    }
}

public static class CatalogValidation
{
    public static IEnumerable<string> MissingPressingDetails(Album record)
    {
        if (record.Medium == RecordMedium.Vinyl && !VinylSizing.HasSize(record.Format)) yield return "vinyl size";
        if (string.IsNullOrWhiteSpace(record.Label)) yield return "label";
        if (string.IsNullOrWhiteSpace(record.CatalogNumber)) yield return "catalogue number";
        if (string.IsNullOrWhiteSpace(record.Country)) yield return "pressing country";
        if (!record.PressingYear.HasValue) yield return "pressing year";
        if (!record.MusicBrainzId.HasValue) yield return "MusicBrainz ID";
        if (!record.DiscogsId.HasValue) yield return "Discogs ID";
    }

    public static bool TryGetVersion(string? value, out byte[] rowVersion)
    {
        rowVersion = new byte[8];
        return value is { Length: 12 } && Convert.TryFromBase64String(value, rowVersion, out var written)
            && written == rowVersion.Length;
    }

    public static IEnumerable<ValidationResult> ValidateVersion(int? id, string? version)
    {
        if (id.HasValue && !TryGetVersion(version, out _))
        {
            yield return new ValidationResult("Reload the page before making changes.");
        }
    }

    public static bool IsCoverUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
}
