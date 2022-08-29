using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.Extensions.Logging;

namespace MattsRecords.Controllers;

public class ArtistController : Controller
{
    private readonly ILogger<ArtistController> _logger;
    private readonly IRecordService _recordService;

    public ArtistController(ILogger<ArtistController> logger, IRecordService rs)
    {
        _logger = logger;
        _recordService = rs;
    }

    public IActionResult Index()
    {

        var artists = _recordService.GetArtists();

        return View(artists);
    }

    public IActionResult Add(){
        return View();
    }

    public IActionResult Create(string name){
        _recordService.AddArtist(name);
        return RedirectToAction("Index");
    }

}
