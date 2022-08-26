using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MattsRecords.Models;
using MattsRecords.Services;


namespace MattsRecords.Controllers;

public class AlbumController : Controller
{
    private readonly ILogger<AlbumController> _logger;
    private readonly IRecordService _recordService;

    public AlbumController(ILogger<AlbumController> logger, IRecordService rs)
    {
        _logger = logger;
        _recordService = rs;
    }

    public IActionResult Index(string? album)
    {

        var albums = _recordService.GetAlbums();

        if(!string.IsNullOrEmpty(album)){
            albums = albums.Where(a => a.Name.ToLower().StartsWith(album.ToLower())).ToList();
        }
        return View(albums);
    }


}
