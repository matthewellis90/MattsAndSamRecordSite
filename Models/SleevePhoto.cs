using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace MattsRecords.Models;

public enum SleeveSide { Front, Back }

public sealed class SleevePhoto
{
    public int RecordId { get; set; }
    public SleeveSide Side { get; set; }
    public Guid Revision { get; set; }
    public byte[] Jpeg { get; set; } = [];
}

public sealed class SleeveForm
{
    [Required]
    public string Version { get; set; } = "";
    [Display(Name = "Front sleeve photo")]
    public IFormFile? Front { get; set; }
    [Display(Name = "Back sleeve photo")]
    public IFormFile? Back { get; set; }
    public bool RemoveFront { get; set; }
    public bool RemoveBack { get; set; }
    [StringLength(100), Display(Name = "Catalogue number")]
    public string? CatalogNumber { get; set; }
    [StringLength(100), Display(Name = "Format details")]
    public string? Format { get; set; }
    [EnumDataType(typeof(VinylSize)), Display(Name = "Vinyl size")]
    public VinylSize Size { get; set; }
    [Display(Name = "Pressing reviewed")]
    public bool Reviewed { get; set; }
    [BindNever, ValidateNever]
    public Album Record { get; set; } = null!;
}

public sealed record SleeveQueue(IReadOnlyList<Album> Items, string Queue, string? Search,
    int Page, int Pages, int Matches, int MissingCovers, int MissingFormats, int MissingSizes, int PendingReviews, int Estimated);
