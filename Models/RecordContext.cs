using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.SqlServer;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;

namespace MattsRecords.Models
{
    public partial class RecordContext : DbContext
    {
        public RecordContext()
        {
        }

        public RecordContext(DbContextOptions<RecordContext> options)
            : base(options)
        {
        }

        public virtual DbSet<Artist> Artists { get; set; } = null!;
        public virtual DbSet<Album> Albums { get; set; } = null!;



        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

            optionsBuilder.UseSqlServer(configuration.GetConnectionString("DefaultConnection"));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {

            modelBuilder.Entity<Artist>(entity =>
            {
                entity.ToTable("artist");

                entity.Property(e => e.Id)
                    .ValueGeneratedNever()
                    .HasColumnName("ID");

                entity.Property(e => e.Name).HasColumnName("artist_name");
            });

        modelBuilder.Entity<Album>(entity =>
            {
                entity.ToTable("album");

                entity.Property(e => e.Id)
                    .ValueGeneratedNever()
                    .HasColumnName("id");

                entity.Property(e => e.ArtistId).HasColumnName("artist_id");
                entity.Property(e => e.ArtistName).HasColumnName("artist_name");
                entity.Property(e => e.Name).HasColumnName("album_name");
                entity.Property(e => e.Year).HasColumnName("year");



            });

            OnModelCreatingPartial(modelBuilder);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
