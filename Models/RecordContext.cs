using Microsoft.EntityFrameworkCore;

namespace MattsRecords.Models;

public sealed class RecordContext(DbContextOptions<RecordContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();

    public DbSet<SleevePhoto> SleevePhotos => Set<SleevePhoto>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Artist>(entity =>
        {
            entity.ToTable("RecordSiteArtist", "dbo");
            entity.Property(artist => artist.Id).HasColumnName("id").UseIdentityColumn();
            entity.Property(artist => artist.Name).HasColumnName("artist_name").HasMaxLength(200);
            entity.Property(artist => artist.RowVersion).HasColumnName("row_version").IsRowVersion();
        });

        modelBuilder.Entity<Album>(entity =>
        {
            entity.ToTable("RecordSiteRelease", "dbo");
            entity.HasOne(album => album.Artist).WithMany().HasForeignKey(album => album.ArtistId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Ignore(album => album.DisplayArtist);
            entity.Property(album => album.Id).HasColumnName("id").UseIdentityColumn();
            entity.Property(album => album.AddedAtUtc).HasColumnName("added_at_utc").HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(album => album.Name).HasColumnName("album_name").HasMaxLength(200);
            entity.Property(album => album.ArtistId).HasColumnName("artist_id");
            entity.Property(album => album.ArtistName).HasColumnName("artist_name").HasMaxLength(200);
            entity.Property(album => album.Year).HasColumnName("year");
            entity.Property(album => album.Kind).HasColumnName("record_kind").HasConversion<int>();
            entity.Property(album => album.Format).HasColumnName("record_format").HasMaxLength(100);
            entity.Property(album => album.Notes).HasColumnName("notes").HasMaxLength(2000);
            entity.Property(album => album.CoverUrl).HasColumnName("cover_url").HasMaxLength(2048);
            entity.Property(album => album.Label).HasColumnName("record_label").HasMaxLength(100);
            entity.Property(album => album.Genre).HasColumnName("genre").HasMaxLength(80);
            entity.Property(album => album.Country).HasColumnName("country").HasMaxLength(80);
            entity.Property(album => album.CatalogNumber).HasColumnName("catalog_number").HasMaxLength(100);
            entity.Property(album => album.Barcode).HasColumnName("barcode").HasMaxLength(32);
            entity.Property(album => album.PressingYear).HasColumnName("pressing_year");
            entity.Property(album => album.MusicBrainzId).HasColumnName("musicbrainz_id");
            entity.Property(album => album.DiscogsId).HasColumnName("discogs_id");
            entity.Property(album => album.FrontSleeveKey).HasColumnName("front_sleeve_key");
            entity.Property(album => album.BackSleeveKey).HasColumnName("back_sleeve_key");
            entity.Property(album => album.PressingReviewed).HasColumnName("pressing_reviewed");
            entity.Ignore(album => album.HasCover);
            entity.Property(album => album.RowVersion).HasColumnName("row_version").IsRowVersion();
        });
        modelBuilder.Entity<SleevePhoto>(entity =>
        {
            entity.ToTable("RecordSiteSleeve", "dbo");
            entity.HasKey(photo => new { photo.RecordId, photo.Side });
            entity.HasOne<Album>().WithMany().HasForeignKey(photo => photo.RecordId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(photo => photo.RecordId).HasColumnName("record_id");
            entity.Property(photo => photo.Side).HasColumnName("side");
            entity.Property(photo => photo.Revision).HasColumnName("revision");
            entity.Property(photo => photo.Jpeg).HasColumnName("jpeg");
        });
    }
}
