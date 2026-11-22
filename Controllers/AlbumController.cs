using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MattsRecords.Controllers;

public sealed class AlbumController(RecordRepository records, PressingLookup pressings, DiscogsLookup discogs,
    ILogger<AlbumController> logger) : Controller
{
    [HttpGet]
    public IActionResult Index(string? album) => RedirectToAction("Index", "Home", new { search = album });

    [HttpGet("records/{id:int}", Order = 0)]
    [HttpGet("Album/Details/{id:int}", Order = 1)]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var record = await records.FindAlbumAsync(id, cancellationToken);
        return record is null ? NotFound() : View(record);
    }

    [Authorize, HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Lookup(string? field, string? term, string? artist, CancellationToken cancellationToken)
    {
        if (field is not ("Title" or "Barcode" or "Catalogue") || string.IsNullOrWhiteSpace(term) ||
            term.Length > 200 || artist?.Length > 100)
            return BadRequest(new { message = "Enter a title, barcode, catalogue number, or MusicBrainz release link." });
        if (field == "Barcode" && RecordBarcode.EquivalentCodes(term).Length == 0)
            return BadRequest(new { message = "Enter the full UPC, EAN, or GTIN printed by the barcode." });
        var collection = await records.SnapshotAsync(cancellationToken);
        var search = term.Trim();
        var title = field == "Title" ? search : "";
        var directId = Guid.TryParse(search.Split('/').LastOrDefault(part => part.Length > 0), out var releaseId)
            ? releaseId : (Guid?)null;
        var rawMatches = new RecordIdentity(title, artist ?? "", field == "Barcode" ? search : null, directId, null)
            .FindMatches(collection, string.IsNullOrWhiteSpace(artist)).Select(match => match.Match).ToArray();
        if (field == "Barcode")
            return await LookupBarcodeAsync(term, artist?.Trim(), collection, rawMatches, cancellationToken);

        try
        {
            return LookupResults(await pressings.FindAsync(field, search, artist?.Trim(), cancellationToken), collection, rawMatches);
        }
        catch (HttpRequestException)
        {
            return StatusCode(503, new { rawMatches, message = "MusicBrainz is unavailable. Try a direct release link or enter the details manually." });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(503, new { rawMatches, message = "The lookup timed out. You can enter the pressing manually." });
        }
    }

    [Authorize, HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> LookupDetails(Guid? id, int? discogsId, string? barcode, CancellationToken cancellationToken)
    {
        try
        {
            if (id is Guid musicBrainzId && musicBrainzId != Guid.Empty && !discogsId.HasValue)
            {
                var candidate = await pressings.GetDetailsAsync(musicBrainzId, cancellationToken);
                return candidate is null ? NotFound() : Json(WithMatches(candidate, await records.SnapshotAsync(cancellationToken)));
            }
            if (discogsId is > 0 && !id.HasValue && barcode is { Length: <= 32 } &&
                RecordBarcode.EquivalentCodes(barcode).Length > 0)
            {
                var candidate = await discogs.GetDetailsAsync(discogsId.Value, barcode, cancellationToken);
                if (candidate is null)
                    return NotFound();
                return Json(WithMatches(await WithAlbumCoverAsync(candidate, cancellationToken),
                    await records.SnapshotAsync(cancellationToken)));
            }
            return BadRequest();
        }
        catch (HttpRequestException)
        {
            return StatusCode(503);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(503);
        }
    }

    private static PressingCandidate WithMatches(PressingCandidate candidate, IReadOnlyList<Album> collection) =>
        candidate with
        {
            CollectionMatches = new RecordIdentity(candidate.Title, candidate.Artist, candidate.Barcode, candidate.Id,
                candidate.DiscogsId).FindMatches(collection).Select(match => match.Match).ToArray()
        };

    private JsonResult LookupResults(IReadOnlyList<PressingCandidate> candidates, IReadOnlyList<Album> collection,
        RecordMatch[] rawMatches) => Json(new { candidates = candidates.Select(candidate => WithMatches(candidate, collection)), rawMatches });

    private async Task<IActionResult> LookupBarcodeAsync(string term, string? artist, IReadOnlyList<Album> collection,
        RecordMatch[] rawMatches, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await pressings.FindAsync("Barcode", term.Trim(), artist, cancellationToken);
            if (candidates.Count > 0)
                return LookupResults(candidates, collection, rawMatches);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "MusicBrainz barcode lookup unavailable. Trying Discogs.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("MusicBrainz barcode lookup timed out. Trying Discogs.");
        }

        try
        {
            var candidates = await discogs.FindBarcodeAsync(term, cancellationToken);
            return LookupResults(candidates, collection, rawMatches);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Discogs barcode lookup unavailable.");
            return StatusCode(503, new { rawMatches, message = "Barcode lookup is temporarily unavailable. Try again or search by title. The barcode stays in the form." });
        }
    }

    private async Task<PressingCandidate> WithAlbumCoverAsync(PressingCandidate candidate, CancellationToken cancellationToken)
    {
        var known = await records.FindByTitleAndCreditAsync(candidate.Title, candidate.Artist, cancellationToken);
        var cover = known?.CoverUrl;
        if (!CatalogValidation.IsCoverUrl(cover))
        {
            try
            {
                cover = await pressings.FindAlbumCoverAsync(candidate.Title, candidate.Artist, cancellationToken);
            }
            catch (HttpRequestException) { cover = null; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { cover = null; }
        }
        return candidate with { CoverUrl = cover, ExistingRecordId = known?.Id, ReleaseYear = candidate.ReleaseYear ?? known?.Year };
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> Add(CancellationToken cancellationToken) =>
        View("Edit", await WithArtistsAsync(new RecordForm(), cancellationToken));

    [Authorize, HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var record = await records.FindAlbumAsync(id, cancellationToken);
        if (record is null)
        {
            return NotFound();
        }

        return View(await WithArtistsAsync(new RecordForm
        {
            Id = record.Id,
            Name = record.Name,
            ArtistId = record.ArtistId,
            Year = record.Year,
            Kind = record.Kind,
            Format = record.Format,
            Notes = record.Notes,
            CoverUrl = record.CoverUrl,
            Label = record.Label,
            Genre = record.Genre,
            Country = record.Country,
            CatalogNumber = record.CatalogNumber,
            Barcode = record.Barcode,
            PressingYear = record.PressingYear,
            MusicBrainzId = record.MusicBrainzId,
            DiscogsId = record.DiscogsId,
            Version = Convert.ToBase64String(record.RowVersion)
        }, cancellationToken));
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Save(RecordForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View("Edit", await WithArtistsAsync(form, cancellationToken));
        }

        var record = new Album
        {
            Id = form.Id.GetValueOrDefault(),
            Name = form.Name.Trim(),
            ArtistId = form.ArtistId,
            Year = form.Year,
            Kind = form.Kind,
            Format = form.Format?.Trim(),
            Notes = form.Notes?.Trim(),
            CoverUrl = form.CoverUrl?.Trim(),
            Label = form.Label,
            Genre = form.Genre,
            Country = form.Country,
            CatalogNumber = form.CatalogNumber,
            Barcode = form.Barcode,
            PressingYear = form.PressingYear,
            MusicBrainzId = form.MusicBrainzId,
            DiscogsId = form.DiscogsId,
            RowVersion = !form.Id.HasValue ? [] : Convert.FromBase64String(form.Version!)
        };

        var result = await records.SaveAlbumAsync(record, form.NewArtistName, form.DuplicateReviewKey, form.DuplicateAction,
            cancellationToken);
        if (result.Change == RecordChange.Success)
        {
            TempData["Status"] = form.Id.HasValue || result.UpdatedExisting ? "Record updated." : "Record added.";
            return RedirectToAction(nameof(Details), new { record.Id });
        }
        if (result.Change == RecordChange.NotFound)
        {
            return NotFound();
        }

        if (result.Change == RecordChange.ReviewRequired)
        {
            form.Review = result.Review;
            if (result.Review is null)
                ModelState.AddModelError(string.Empty, "The collection changed. Review this record and save again.");
        }
        else if (result.Change == RecordChange.ArtistRequired)
        {
            ModelState.AddModelError(nameof(form.ArtistId), "Choose an artist from the list or enter a new artist name.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "This record changed while you were editing it. Open the record again before saving.");
        }
        return View("Edit", await WithArtistsAsync(form, cancellationToken));
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var record = await records.FindAlbumAsync(id, cancellationToken);
        return record is null ? NotFound() : View(new DeleteForm(record.Id, record.Name,
            Convert.ToBase64String(record.RowVersion), "record"));
    }

    [Authorize, HttpPost, ActionName(nameof(Delete))]
    public async Task<IActionResult> ConfirmDelete(int id, string? version, CancellationToken cancellationToken)
    {
        if (!CatalogValidation.TryGetVersion(version, out var rowVersion))
        {
            return BadRequest();
        }

        var result = await records.DeleteAlbumAsync(id, rowVersion, cancellationToken);
        if (result == RecordChange.Success)
        {
            TempData["Status"] = "Record removed.";
            return RedirectToAction("Index", "Home");
        }
        if (result == RecordChange.NotFound)
        {
            return NotFound();
        }

        TempData["Status"] = "The record changed. Review it before removing it.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<RecordForm> WithArtistsAsync(RecordForm form, CancellationToken cancellationToken)
    {
        form.Artists = await records.ListArtistsAsync(null, cancellationToken);
        return form;
    }
}
