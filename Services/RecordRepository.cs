using MattsRecords.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;


namespace MattsRecords.Services
{
    public interface IRecordRepository
    {
        List<Artist> GetArtists();
        List<Album> GetAlbums();
        void AddArtist(string name);
    }

    public class RecordRepository : IRecordRepository
    {
        private readonly RecordContext _context;

        public RecordRepository(RecordContext ctx){
            _context = ctx;
        }

        public List<Artist> GetArtists(){
            return _context.Artists.ToList();
        }

        public List<Album> GetAlbums(){
            return _context.Albums.ToList();
        }

        public void AddArtist(string name){
            var id = _context.Artists.OrderByDescending(a => a.Id).First().Id +1;
            _context.Artists.Add(new Artist{
                Id= id,
                Name=name
            });

            _context.SaveChanges();
        }

    }
}
