using MattsRecords.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace MattsRecords.Services
{
    public interface IRecordService
    {
        List<Artist> GetArtists();
        List<Album> GetAlbums();
        void AddArtist(string name);
    }

    public class RecordService : IRecordService{

        public readonly IRecordRepository _repo;

        public RecordService(IRecordRepository repo){
            _repo = repo;
        }
        public List<Artist> GetArtists() => _repo.GetArtists();
        public List<Album> GetAlbums() => _repo.GetAlbums();
        public void AddArtist(string name) => _repo.AddArtist(name);

    }
}
