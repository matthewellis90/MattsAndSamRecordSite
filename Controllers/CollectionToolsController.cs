using System.Globalization;
using System.Text;
using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MattsRecords.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CollectionToolsController(RecordRepository records) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? queue, string? search, int page, CancellationToken cancellationToken)
    {
        var entries = await records.SnapshotAsync(cancellationToken);
        var selected = queue is "cover" or "format" or "all" ? queue : "review";
        var terms = CatalogSearch.Terms(search);
        var matches = entries.Where(record => selected switch
            {
                "cover" => !record.HasCover,
                "format" => !record.HasVinylFormatDetails,
                "all" => true,
                _ => !record.PressingReviewed
            }).Where(record => terms.Length == 0 || CatalogSearch.Matches(record, terms))
            .OrderBy(record => record.HasCover).ThenBy(record => record.Name).ThenBy(record => record.Id).ToList();
        const int pageSize = 24;
        var pages = Math.Max(1, (matches.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        return View(new SleeveQueue(matches.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            selected, search, page, pages, matches.Count,
            entries.Count(record => !record.HasCover), entries.Count(record => !record.HasVinylFormatDetails),
            entries.Count(record => !record.PressingReviewed)));
    }

    [HttpGet]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var snapshot = await records.SnapshotAsync(cancellationToken);
        var csv = new StringBuilder("Id,Title,Artist,Type,Release year,Format,Label,Genre,Country,Pressing year,Catalogue number,Barcode,MusicBrainz ID,Cover URL,Notes,Front sleeve path,Back sleeve path,Pressing reviewed\r\n");
        foreach (var record in snapshot)
        {
            csv.AppendJoin(',', new[] {
                record.Id.ToString(CultureInfo.InvariantCulture), record.Name, record.DisplayArtist, record.Kind.ToString(),
                record.Year?.ToString(CultureInfo.InvariantCulture), record.Format, record.Label, record.Genre, record.Country,
                record.PressingYear?.ToString(CultureInfo.InvariantCulture), record.CatalogNumber, record.Barcode,
                record.MusicBrainzId?.ToString(), record.CoverUrl, record.Notes,
                record.FrontSleeveKey is Guid front ? Url.RouteUrl("SleeveImage", new { id = record.Id, side = 0, revision = front }) : null,
                record.BackSleeveKey is Guid back ? Url.RouteUrl("SleeveImage", new { id = record.Id, side = 1, revision = back }) : null,
                record.PressingReviewed ? "Yes" : "No"
            }.Select(EscapeCell)).Append("\r\n");
        }
        return File(Encoding.UTF8.GetBytes("\uFEFF" + csv), "text/csv; charset=utf-8", SiteBrand.ExportFileName);
    }

    private static string EscapeCell(string? cell)
    {
        cell ??= "";
        var value = cell.TrimStart();
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            cell = "'" + cell;
        return "\"" + cell.Replace("\"", "\"\"") + "\"";
    }
}
