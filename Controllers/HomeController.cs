using System.Diagnostics;
using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Mvc;

namespace MattsRecords.Controllers;

public sealed class HomeController(RecordRepository records) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([Bind("Search,Kind,ArtistId,Sort,Page,Decade,Format,Label,Genre,List")] RecordQuery query,
        CancellationToken cancellationToken)
    {
        var page = await records.BrowseAsync(query, cancellationToken);
        var artists = await records.ListArtistsAsync(null, cancellationToken);
        var recent = query.ShowRecent
            ? (await records.BrowseAsync(new RecordQuery { Sort = RecordSort.Recent, PageSize = 10 }, cancellationToken)).Items
            : [];
        return View(new CatalogView(query, page, artists, await records.GetFacetsAsync(cancellationToken), recent));
    }

    [HttpGet]
    public async Task<IActionResult> Suggest(string? search, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2)
            return Json(Array.Empty<object>());

        var page = await records.BrowseAsync(new RecordQuery { Search = search, PageSize = 6 }, cancellationToken);
        return Json(page.Items.Select(record => new { record.Name, Artist = record.DisplayArtist, record.Year,
            Url = Url.Action("Details", "Album", new { record.Id }) }));
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Surprise(CancellationToken cancellationToken)
    {
        var id = await records.PickAsync(cancellationToken);
        return id is null ? RedirectToAction(nameof(Index)) : RedirectToAction("Details", "Album", new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken) =>
        View(await records.SnapshotAsync(cancellationToken));

    [HttpGet]
    public IActionResult Privacy() => View();

    [IgnoreAntiforgeryToken]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
    });
}
