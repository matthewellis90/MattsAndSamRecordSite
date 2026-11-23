using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MattsRecords.Controllers;

public sealed class ArtistController(RecordRepository records) : Controller
{
    [Authorize, HttpGet("artists", Order = 0)]
    [HttpGet("Artist", Order = 1)]
    [HttpGet("Artist/Index", Order = 2)]
    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken) =>
        View(new ArtistsView(search, await records.ListArtistsAsync(search, cancellationToken)));

    [Authorize, HttpGet]
    public IActionResult Add() => View("Edit", new ArtistForm());

    [Authorize, HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var artist = await records.FindArtistAsync(id, cancellationToken);
        return artist is null ? NotFound() : View(new ArtistForm
        {
            Id = artist.Id,
            Name = artist.Name,
            Version = Convert.ToBase64String(artist.RowVersion)
        });
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Create(ArtistForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View("Edit", form);
        }

        var result = await records.SaveArtistAsync(new Artist
        {
            Id = form.Id.GetValueOrDefault(),
            Name = form.Name.Trim(),
            RowVersion = !form.Id.HasValue ? [] : Convert.FromBase64String(form.Version!)
        }, cancellationToken);
        if (result == RecordChange.Success)
        {
            TempData["Status"] = !form.Id.HasValue ? "Artist added." : "Artist updated.";
            return RedirectToAction(nameof(Index));
        }
        if (result == RecordChange.NotFound)
        {
            return NotFound();
        }

        if (result == RecordChange.Duplicate)
        {
            ModelState.AddModelError(nameof(form.Name), "An artist with that name already exists.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "This artist changed while you were editing it. Open the artist again before saving.");
        }
        return View("Edit", form);
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var artist = await records.FindArtistAsync(id, cancellationToken);
        return artist is null ? NotFound() : View("~/Views/Album/Delete.cshtml",
            new DeleteForm(artist.Id, artist.Name, Convert.ToBase64String(artist.RowVersion), "artist"));
    }

    [Authorize, HttpPost, ActionName(nameof(Delete))]
    public async Task<IActionResult> ConfirmDelete(int id, string? version, CancellationToken cancellationToken)
    {
        if (!CatalogValidation.TryGetVersion(version, out var rowVersion))
        {
            return BadRequest();
        }

        var result = await records.DeleteArtistAsync(id, rowVersion, cancellationToken);
        if (result == RecordChange.NotFound)
        {
            return NotFound();
        }

        TempData["Status"] = result switch
        {
            RecordChange.Success => "Artist removed.",
            RecordChange.ArtistInUse => "Move or remove this artist’s records before removing the artist.",
            _ => "The artist changed. Review it before removing it."
        };
        return RedirectToAction(nameof(Index));
    }
}
