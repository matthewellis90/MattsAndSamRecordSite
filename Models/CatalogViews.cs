using System.ComponentModel.DataAnnotations;
using MattsRecords.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MattsRecords.Models;

public sealed record CatalogView(RecordQuery Query, RecordPage Results, IReadOnlyList<ArtistSummary> Artists,
    CatalogFacets Facets, IReadOnlyList<Album> Recent);

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

    [StringLength(100)]
    public string? Format { get; set; }

    [StringLength(2000)]
    public string? Notes { get; set; }

    [Display(Name = "Cover image URL"), StringLength(2048)]
    public string? CoverUrl { get; set; }

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
    }
}

public static class CatalogValidation
{
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
