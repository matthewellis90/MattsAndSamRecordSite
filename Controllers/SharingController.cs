using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MattsRecords.Controllers;

[Authorize]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class SharingController(ICollectionSharing sharing, CollectionDatabases databases) : Controller
{
    [HttpGet("/Account/Sharing")]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        if (!CollectionSession.IsPersonal(User))
            return View(nameof(Settings), model: null);

        if (!CollectionSession.IsReady(User) || CollectionSession.GetId(User) is not Guid id)
            return Forbid();

        var token = await sharing.ReadAsync(id, cancellationToken);
        return View(nameof(Settings),
            token is Guid value ? Url.RouteUrl("SharedCollection", new { token = value }, Request.Scheme) : null);
    }

    [HttpPost("/Account/Sharing/Create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        if (!CollectionSession.IsReady(User) || CollectionSession.GetId(User) is not Guid id)
            return Forbid();

        await sharing.SetAsync(id, Guid.NewGuid(), cancellationToken);
        TempData["Status"] = "Your new sharing link is ready.";
        return RedirectToAction(nameof(Settings));
    }

    [HttpPost("/Account/Sharing/Disable")]
    public async Task<IActionResult> Disable(CancellationToken cancellationToken)
    {
        if (!CollectionSession.IsReady(User) || CollectionSession.GetId(User) is not Guid id)
            return Forbid();

        await sharing.SetAsync(id, null, cancellationToken);
        TempData["Status"] = "Collection sharing is off.";
        return RedirectToAction(nameof(Settings));
    }

    [AllowAnonymous, HttpGet("/shared/{token:guid}", Name = "SharedCollection")]
    public async Task<IActionResult> Browse(Guid token, string? search, RecordMedium? medium, int page, CancellationToken cancellationToken)
    {
        if (await sharing.FindAsync(token, cancellationToken) is not { } share)
            return NotFound();

        var query = new RecordQuery { Search = search is { Length: > 200 } ? search[..200] : search, Medium = medium, Page = page };
        await using var catalog = databases.OpenPersonalCatalog(share.AccountId);
        var repository = new RecordRepository(catalog);
        var results = await repository.BrowseAsync(query, cancellationToken);
        var isFiltered = query.HasFilters;
        var total = isFiltered ? await catalog.Albums.CountAsync(cancellationToken) : results.TotalCount;
        var recent = !isFiltered && results.Page == 1 ? await repository.RecentAsync(6, cancellationToken) : [];
        return View(new SharedCatalogView(token, share.Username, query.Search, results, total, recent, query.Medium));
    }

    [AllowAnonymous, HttpGet("/shared/{token:guid}/records/{id:int}", Name = "SharedRecord")]
    public async Task<IActionResult> Record(Guid token, int id, CancellationToken cancellationToken)
    {
        if (await sharing.FindAsync(token, cancellationToken) is not { } share)
            return NotFound();

        await using var catalog = databases.OpenPersonalCatalog(share.AccountId);
        var record = await new RecordRepository(catalog).FindAlbumAsync(id, cancellationToken);
        return record is null ? NotFound() : View(new SharedRecordView(token, record));
    }

    [AllowAnonymous, HttpGet("/shared/{token:guid}/records/{id:int}/sleeve/{side:int}/{revision:guid}", Name = "SharedSleeve")]
    public async Task<IActionResult> Image(Guid token, int id, SleeveSide side, Guid revision, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(side) ||
            await sharing.FindAsync(token, cancellationToken) is not { } share)
            return NotFound();

        await using var catalog = databases.OpenPersonalCatalog(share.AccountId);
        var jpeg = await new RecordRepository(catalog).GetSleeveAsync(id, side, revision, cancellationToken);
        return jpeg is null ? NotFound() : File(jpeg, "image/jpeg");
    }
}
