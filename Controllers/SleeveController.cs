using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MattsRecords.Controllers;

public sealed class SleeveController(RecordRepository records) : Controller
{
    [HttpGet("/records/{id:int}/sleeve/{side:int}/{revision:guid}", Name = "SleeveImage")]
    public async Task<IActionResult> Image(int id, SleeveSide side, Guid revision, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(side))
            return NotFound();

        var jpeg = await records.GetSleeveAsync(id, side, revision, cancellationToken);
        if (jpeg is null)
            return NotFound();

        Response.Headers.CacheControl = CollectionSession.IsPersonal(User) ? "private, no-store" : "public, max-age=3600";
        return File(jpeg, "image/jpeg");
    }

    [Authorize, HttpGet("/records/{id:int}/sleeves")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var record = await records.FindAlbumAsync(id, cancellationToken);
        return record is null ? NotFound() : View(new SleeveForm
        {
            Record = record,
            Version = Convert.ToBase64String(record.RowVersion),
            CatalogNumber = record.CatalogNumber,
            Format = record.Format,
            Reviewed = record.PressingReviewed
        });
    }

    [Authorize, HttpPost("/records/{id:int}/sleeves")]
    [RequestSizeLimit(SleeveImages.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = SleeveImages.MaxFileBytes + 4096)]
    public async Task<IActionResult> Save(int id, SleeveForm form, CancellationToken cancellationToken)
    {
        var record = await records.FindAlbumAsync(id, cancellationToken);
        if (record is null)
            return NotFound();
        form.Record = record;

        if (!CatalogValidation.TryGetVersion(form.Version, out var version))
            ModelState.AddModelError(string.Empty, "Reload this record before saving.");
        if ((form.RemoveFront && form.Front is not null) || (form.RemoveBack && form.Back is not null))
            ModelState.AddModelError(string.Empty, "Choose a replacement or removal for each sleeve.");

        if (!ModelState.IsValid)
            return View(nameof(Edit), form);

        var front = await ReadAsync(form.Front, nameof(form.Front), cancellationToken);
        var back = await ReadAsync(form.Back, nameof(form.Back), cancellationToken);
        if (!ModelState.IsValid)
            return View(nameof(Edit), form);

        // Forms opened before the format field was added can still save their other changes.
        if (!ModelState.ContainsKey(nameof(form.Format)))
            form.Format = record.Format;

        var result = await records.SaveSleevesAsync(id, version, form, front, back, cancellationToken);
        if (result == RecordChange.NotFound)
            return NotFound();
        if (result != RecordChange.Success)
        {
            ModelState.AddModelError(string.Empty, "This record changed. Reload the page and review the latest details before saving.");
            return View(nameof(Edit), form);
        }

        TempData["Status"] = "Sleeves and pressing review saved.";
        return RedirectToAction("Details", "Album", new { id });
    }

    private async Task<byte[]?> ReadAsync(IFormFile? file, string field, CancellationToken cancellationToken)
    {
        if (file is null)
            return null;

        try
        {
            return await SleeveImages.ReencodeAsync(file, cancellationToken);
        }
        catch (InvalidDataException error)
        {
            ModelState.AddModelError(field, error.Message);
            return null;
        }
    }
}
