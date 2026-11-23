using System.Diagnostics;
using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MattsRecords.Controllers;

[Authorize]
public sealed class HomeController(RecordRepository records, ICollectionSharing sharing) : Controller
{
    private static readonly RecordMedium[] MediaOrder = [RecordMedium.Vinyl, RecordMedium.Cassette, RecordMedium.CD];

    [AllowAnonymous, HttpGet]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Index([Bind("Search,Kind,Medium,ArtistId,Sort,Page,Decade,Format,Label,Genre,List")] RecordQuery query,
        RecordMedium? section, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true)
            return View("Welcome");

        var media = query.EffectiveMedium is RecordMedium selected ? [selected] : MediaOrder;
        var pagedMedium = query.EffectiveMedium ?? section ?? media[0];
        async Task<List<CatalogSection>> ReadSectionsAsync(bool allowFuzzyMatch)
        {
            var result = new List<CatalogSection>(media.Length);
            foreach (var medium in media)
            {
                var page = await records.BrowseAsync(query with { Medium = medium, Page = medium == pagedMedium ? query.Page : 1 },
                    cancellationToken, allowFuzzyMatch);
                result.Add(new CatalogSection(medium, page));
            }
            return result;
        }
        var sections = await ReadSectionsAsync(allowFuzzyMatch: media.Length == 1);
        if (media.Length > 1 && !string.IsNullOrWhiteSpace(query.Search) && sections.All(item => item.Results.TotalCount == 0))
            sections = await ReadSectionsAsync(allowFuzzyMatch: true);
        var artists = await records.ListArtistsAsync(null, cancellationToken);
        var recent = query.ShowRecent
            ? (await records.BrowseAsync(new RecordQuery { Sort = RecordSort.Recent, PageSize = 10 }, cancellationToken)).Items
            : [];
        var shareToken = CollectionSession.IsReady(User) && CollectionSession.GetId(User) is Guid id
            ? await sharing.ReadAsync(id, cancellationToken) : null;
        return View(new CatalogView(query, sections, section ?? (query.Page > 1 ? pagedMedium : null), artists,
            await records.GetFacetsAsync(cancellationToken), recent, shareToken));
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

    [AllowAnonymous, HttpGet]
    public IActionResult Privacy() => View();

    [AllowAnonymous, HttpGet("/faq")]
    public IActionResult Faq() => View();

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
    });
}
