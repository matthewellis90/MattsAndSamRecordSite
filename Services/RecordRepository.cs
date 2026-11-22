using System.Data;
using MattsRecords.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MattsRecords.Services;

public enum RecordSort
{
    Recent,
    Title,
    Artist,
    Year
}

public enum RecordChange
{
    Success,
    NotFound,
    Conflict,
    Duplicate,
    ReviewRequired,
    ArtistRequired,
    ArtistInUse
}

public sealed class RecordQuery
{
    public const string UnrecordedFormat = "~unrecorded";

    public string? Search { get; init; }
    public RecordKind? Kind { get; init; }
    public int? ArtistId { get; init; }
    public int? Decade { get; init; }
    public string? Format { get; init; }
    public string? Genre { get; init; }
    public string? Label { get; init; }
    public bool List { get; init; }
    public RecordSort Sort { get; init; } = RecordSort.Title;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 24;
    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || Kind.HasValue || ArtistId.HasValue || Decade.HasValue ||
        !string.IsNullOrWhiteSpace(Format) || !string.IsNullOrWhiteSpace(Genre) || !string.IsNullOrWhiteSpace(Label);
    public bool ShowRecent => !HasFilters && Page <= 1 && Sort == RecordSort.Title && !List;
}

public sealed record RecordPage(IReadOnlyList<Album> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

public sealed record ArtistSummary(int Id, string Name, int RecordCount);

public sealed class RecordRepository(RecordContext context)
{
    private const string SearchCollation = "Latin1_General_100_CI_AI_SC";

    public async Task<RecordPage> BrowseAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        IQueryable<Album> records = context.Albums.AsNoTracking().Include(album => album.Artist);
        if (query.Kind is { } kind)
        {
            records = records.Where(record => record.Kind == kind);
        }

        if (query.ArtistId is { } artistId)
        {
            records = records.Where(record => record.ArtistId == artistId);
        }

        if (query.Decade is >= 1000 and <= 9990 && query.Decade % 10 == 0)
        {
            var start = query.Decade.Value;
            records = records.Where(record => record.Year >= start && record.Year < start + 10);
        }
        var format = query.Format?.Trim();
        if (format == RecordQuery.UnrecordedFormat)
        {
            records = records.Where(record => record.Format == null || record.Format.Trim() == "" ||
                EF.Functions.Collate(record.Format.Trim(), SearchCollation) == Album.VinylFormat);
        }
        else if (!string.IsNullOrEmpty(format) && !string.Equals(format, Album.VinylFormat, StringComparison.OrdinalIgnoreCase))
        {
            records = records.Where(record => record.Format != null &&
                EF.Functions.Collate(record.Format.Trim(), SearchCollation) == format);
        }
        if (!string.IsNullOrWhiteSpace(query.Genre))
            records = records.Where(record => record.Genre == query.Genre);
        if (!string.IsNullOrWhiteSpace(query.Label))
            records = records.Where(record => record.Label == query.Label);

        var filtered = records;
        var terms = CatalogSearch.Terms(query.Search);
        foreach (var term in terms)
        {
            if (term.Length == 4 && int.TryParse(term, out var year))
                records = records.Where(record => record.Year == year || record.PressingYear == year ||
                    record.Name.Contains(term) || (record.CatalogNumber != null && record.CatalogNumber.Contains(term)));
            else
                records = records.Where(record =>
                    EF.Functions.Collate(record.Name, SearchCollation).Contains(term) ||
                    EF.Functions.Collate(record.Artist == null ? record.ArtistName ?? "Uncredited" : record.Artist.Name, SearchCollation).Contains(term) ||
                    (record.Label != null && record.Label.Contains(term)) ||
                    (record.Genre != null && record.Genre.Contains(term)) ||
                    (record.Format != null && record.Format.Contains(term)) ||
                    (record.CatalogNumber != null && record.CatalogNumber.Contains(term)) ||
                    (record.Barcode != null && record.Barcode.Contains(term)));
        }

        var ordered = query.Sort switch
        {
            RecordSort.Title => records.OrderBy(record => record.Name),
            RecordSort.Artist => records.OrderBy(record =>
                record.Artist == null ? record.ArtistName ?? "Uncredited" : record.Artist.Name).ThenBy(record => record.Name),
            RecordSort.Year => records.OrderByDescending(record => record.Year).ThenBy(record => record.Name),
            _ => records.OrderByDescending(record => record.AddedAtUtc).ThenByDescending(record => record.Id)
        };

        var count = await records.CountAsync(cancellationToken);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        if (count == 0 && terms.Length > 0)
        {
            var candidates = await filtered.OrderBy(record => record.Name).ThenBy(record => record.Id)
                .Take(4000).ToListAsync(cancellationToken);
            var similar = candidates.Where(record => CatalogSearch.Matches(record, terms)).ToList();
            var sorted = query.Sort switch
            {
                RecordSort.Artist => similar.OrderBy(record => record.DisplayArtist).ThenBy(record => record.Name),
                RecordSort.Year => similar.OrderByDescending(record => record.Year).ThenBy(record => record.Name),
                RecordSort.Recent => similar.OrderByDescending(record => record.AddedAtUtc).ThenByDescending(record => record.Id),
                _ => similar.OrderBy(record => record.Name)
            };
            var fallbackPage = Math.Clamp(query.Page, 1, Math.Max(1, (similar.Count + pageSize - 1) / pageSize));
            return new RecordPage(sorted.ThenBy(record => record.Id).Skip((fallbackPage - 1) * pageSize)
                .Take(pageSize).ToList(), similar.Count, fallbackPage, pageSize);
        }
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling((double)count / pageSize)));
        var rows = await ordered.ThenBy(record => record.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new RecordPage(rows, count, page, pageSize);
    }

    public async Task<CatalogFacets> GetFacetsAsync(CancellationToken cancellationToken)
    {
        var rows = await context.Albums.AsNoTracking()
            .Select(record => new { record.Year, record.Format, record.Label, record.Genre })
            .Distinct().ToListAsync(cancellationToken);
        return new CatalogFacets(rows.Where(row => row.Year >= 1000)
            .Select(row => row.Year!.Value / 10 * 10).Distinct().OrderDescending().ToArray(),
            Names(rows.Select(row => row.Format?.Trim()).Where(Album.IsDetailedVinylFormat)),
            Names(rows.Select(row => row.Genre)), Names(rows.Select(row => row.Label)));
    }

    public Task<int?> PickAsync(CancellationToken cancellationToken) =>
        context.Albums.AsNoTracking().OrderBy(record => Guid.NewGuid()).Select(record => (int?)record.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Album>> SnapshotAsync(CancellationToken cancellationToken) =>
        await context.Albums.AsNoTracking().Include(record => record.Artist)
            .OrderBy(record => record.Name).ThenBy(record => record.Id).ToListAsync(cancellationToken);

    public Task<Album?> FindByTitleAndCreditAsync(string title, string artist, CancellationToken cancellationToken) =>
        context.Albums.AsNoTracking().Where(record =>
            EF.Functions.Collate(record.Name, SearchCollation) == title &&
            EF.Functions.Collate(record.Artist == null ? record.ArtistName ?? "" : record.Artist.Name, SearchCollation) == artist)
            .OrderByDescending(record => record.CoverUrl != null)
            .ThenBy(record => record.Id).FirstOrDefaultAsync(cancellationToken);

    private static string[] Names(IEnumerable<string?> values) => values.OfType<string>()
        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();

    public Task<Album?> FindAlbumAsync(int id, CancellationToken cancellationToken) =>
        context.Albums.AsNoTracking().Include(album => album.Artist)
            .SingleOrDefaultAsync(album => album.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ArtistSummary>> ListArtistsAsync(string? search, CancellationToken cancellationToken)
    {
        var artists = context.Artists.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            artists = artists.Where(artist => EF.Functions.Collate(artist.Name, SearchCollation).Contains(term));
        }

        return await artists.OrderBy(artist => artist.Name)
            .ThenBy(artist => artist.Id)
            .Select(artist => new ArtistSummary(
                artist.Id, artist.Name, context.Albums.Count(album => album.ArtistId == artist.Id)))
            .ToListAsync(cancellationToken);
    }

    public Task<byte[]?> GetSleeveAsync(int id, SleeveSide side, Guid revision, CancellationToken cancellationToken) =>
        context.SleevePhotos.AsNoTracking()
            .Where(photo => photo.RecordId == id && photo.Side == side && photo.Revision == revision)
            .Select(photo => photo.Jpeg).SingleOrDefaultAsync(cancellationToken);

    public async Task<RecordChange> SaveSleevesAsync(int id, byte[] version, SleeveForm form, byte[]? front, byte[]? back,
        CancellationToken cancellationToken)
    {
        var record = await context.Albums.SingleOrDefaultAsync(album => album.Id == id, cancellationToken);
        if (record is null)
            return RecordChange.NotFound;
        if (!record.RowVersion.AsSpan().SequenceEqual(version))
            return RecordChange.Conflict;

        if (front is not null || form.RemoveFront)
            record.FrontSleeveKey = await SetSleeveAsync(id, SleeveSide.Front, front, cancellationToken);
        if (back is not null || form.RemoveBack)
            record.BackSleeveKey = await SetSleeveAsync(id, SleeveSide.Back, back, cancellationToken);

        record.CatalogNumber = Clean(form.CatalogNumber);
        record.Format = Clean(form.Format);
        record.PressingReviewed = form.Reviewed;
        return await PersistAsync(record, RecordChange.Conflict, cancellationToken);
    }

    private async Task<Guid?> SetSleeveAsync(int id, SleeveSide side, byte[]? jpeg, CancellationToken cancellationToken)
    {
        var photo = await context.SleevePhotos.SingleOrDefaultAsync(item => item.RecordId == id && item.Side == side,
            cancellationToken);
        if (jpeg is null)
        {
            if (photo is not null)
                context.SleevePhotos.Remove(photo);
            return null;
        }

        if (photo is null)
        {
            photo = new SleevePhoto { RecordId = id, Side = side };
            context.SleevePhotos.Add(photo);
        }
        photo.Revision = Guid.NewGuid();
        photo.Jpeg = jpeg;
        return photo.Revision;
    }

    public Task<Artist?> FindArtistAsync(int id, CancellationToken cancellationToken) =>
        context.Artists.AsNoTracking().SingleOrDefaultAsync(artist => artist.Id == id, cancellationToken);

    public async Task<RecordSave> SaveAlbumAsync(Album album, string? newArtistName, string? reviewKey, string? action,
        CancellationToken cancellationToken)
    {
        if (album.RowVersion.Length != 0)
            return new RecordSave(await SaveAlbumCoreAsync(album, null, cancellationToken));

        try
        {
            // Keep the duplicate check and write in one transaction so a second add can't slip between them.
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var artistName = Clean(newArtistName);
            var credit = album.ArtistId is int artistId
                ? (await FindArtistAsync(artistId, cancellationToken))?.Name
                : artistName;
            var identity = new RecordIdentity(album.Name, credit ?? "", album.Barcode, album.MusicBrainzId, album.DiscogsId);
            var matches = identity.FindMatches(await SnapshotAsync(cancellationToken));
            if (matches.Length > 0)
            {
                var review = identity.Review(matches);
                if (reviewKey != review.Key)
                    return new RecordSave(RecordChange.ReviewRequired, review);

                if (action != "copy")
                {
                    var selected = matches.FirstOrDefault(item => action == $"update-{item.Record.Id}").Record;
                    if (selected is null)
                        return new RecordSave(RecordChange.ReviewRequired, review);

                    var result = await MergeAlbumAsync(album, selected, cancellationToken);
                    if (result == RecordChange.Success)
                        await transaction.CommitAsync(cancellationToken);
                    return new RecordSave(result, UpdatedExisting: result == RecordChange.Success);
                }
            }
            else if (!string.IsNullOrEmpty(action))
                return new RecordSave(RecordChange.ReviewRequired);

            Artist? artist = null;
            if (artistName is not null && !album.ArtistId.HasValue)
            {
                artist = await context.Artists.OrderBy(existing => existing.Id)
                    .FirstOrDefaultAsync(existing => EF.Functions.Collate(existing.Name, SearchCollation) == artistName,
                        cancellationToken);
                if (artist is null)
                {
                    artist = new Artist { Name = artistName };
                    context.Artists.Add(artist);
                }
            }

            var added = await SaveAlbumCoreAsync(album, artist, cancellationToken);
            if (added == RecordChange.Success)
                await transaction.CommitAsync(cancellationToken);
            return new RecordSave(added);
        }
        catch (Exception exception) when (exception is SqlException { Number: 1205 } ||
            exception is DbUpdateException { InnerException: SqlException { Number: 1205 } })
        {
            return new RecordSave(RecordChange.Conflict);
        }
    }

    private async Task<RecordChange> MergeAlbumAsync(Album incoming, Album reviewed, CancellationToken cancellationToken)
    {
        var target = await context.Albums.SingleOrDefaultAsync(record => record.Id == reviewed.Id, cancellationToken);
        if (target is null || !target.RowVersion.AsSpan().SequenceEqual(reviewed.RowVersion))
            return RecordChange.Conflict;

        var original = (target.Format, target.Label, target.Country, target.CatalogNumber,
            target.Barcode, target.PressingYear, target.MusicBrainzId, target.DiscogsId);
        target.Format = Clean(incoming.Format) ?? target.Format;
        target.Label = Clean(incoming.Label) ?? target.Label;
        target.Country = Clean(incoming.Country) ?? target.Country;
        target.CatalogNumber = Clean(incoming.CatalogNumber) ?? target.CatalogNumber;
        target.Barcode = Clean(incoming.Barcode) ?? target.Barcode;
        target.PressingYear = incoming.PressingYear ?? target.PressingYear;
        if (incoming.MusicBrainzId.HasValue || incoming.DiscogsId.HasValue)
            (target.MusicBrainzId, target.DiscogsId) = (incoming.MusicBrainzId, incoming.DiscogsId);
        if (original != (target.Format, target.Label, target.Country, target.CatalogNumber,
            target.Barcode, target.PressingYear, target.MusicBrainzId, target.DiscogsId))
            target.PressingReviewed = false;

        target.Year ??= incoming.Year;
        target.Genre = Clean(target.Genre) ?? Clean(incoming.Genre);
        target.Notes = Clean(target.Notes) is null ? Clean(incoming.Notes) : target.Notes;
        if (!target.HasCover && CatalogValidation.IsCoverUrl(incoming.CoverUrl))
            target.CoverUrl = incoming.CoverUrl;

        var result = await PersistAsync(target, RecordChange.Conflict, cancellationToken);
        if (result == RecordChange.Success)
            incoming.Id = target.Id;
        return result;
    }

    private async Task<RecordChange> SaveAlbumCoreAsync(Album album, Artist? suppliedArtist, CancellationToken cancellationToken)
    {
        var updating = album.RowVersion.Length != 0;
        Album target;
        if (!updating)
        {
            target = new Album();
        }
        else
        {
            var existing = await context.Albums.SingleOrDefaultAsync(record => record.Id == album.Id, cancellationToken);
            if (existing is null)
            {
                return RecordChange.NotFound;
            }

            if (!existing.RowVersion.AsSpan().SequenceEqual(album.RowVersion))
            {
                return RecordChange.Conflict;
            }

            target = existing;
        }

        var artist = album.ArtistId is { } artistId
            ? await FindArtistAsync(artistId, cancellationToken)
            : suppliedArtist;
        var preserveLegacyCredit = updating && target.ArtistId is null && !string.IsNullOrWhiteSpace(target.ArtistName);
        if (artist is null && (album.ArtistId.HasValue || album.Kind != RecordKind.Compilation && !preserveLegacyCredit))
        {
            return RecordChange.ArtistRequired;
        }

        if (target.Name != album.Name.Trim() || target.ArtistId != artist?.Id ||
            target.PressingYear != album.PressingYear || target.MusicBrainzId != album.MusicBrainzId ||
            target.DiscogsId != album.DiscogsId ||
            target.Label != Clean(album.Label) || target.Country != Clean(album.Country) ||
            target.Format != Clean(album.Format) || target.Barcode != Clean(album.Barcode) ||
            target.CatalogNumber != Clean(album.CatalogNumber))
            target.PressingReviewed = false;

        target.Name = album.Name.Trim();
        target.ArtistId = artist?.Id;
        target.ArtistName = artist?.Name ?? (preserveLegacyCredit ? target.ArtistName : null);
        target.Year = album.Year;
        target.Kind = album.Kind;
        target.Format = Clean(album.Format);
        target.Notes = Clean(album.Notes);
        target.CoverUrl = Clean(album.CoverUrl);
        target.Label = Clean(album.Label);
        target.Genre = Clean(album.Genre);
        target.Country = Clean(album.Country);
        target.CatalogNumber = Clean(album.CatalogNumber);
        target.Barcode = Clean(album.Barcode);
        target.PressingYear = album.PressingYear;
        target.MusicBrainzId = album.MusicBrainzId;
        target.DiscogsId = album.DiscogsId;

        if (!updating)
        {
            if (suppliedArtist is not null)
                target.Artist = suppliedArtist;
            context.Albums.Add(target);
        }

        var result = await PersistAsync(target, RecordChange.ArtistRequired, cancellationToken);
        if (result == RecordChange.Success)
        {
            album.Id = target.Id;
            album.RowVersion = target.RowVersion;
        }

        return result;
    }

    public async Task<RecordChange> DeleteAlbumAsync(int id, byte[] rowVersion, CancellationToken cancellationToken)
    {
        var album = await context.Albums.SingleOrDefaultAsync(record => record.Id == id, cancellationToken);
        if (album is null)
        {
            return RecordChange.NotFound;
        }

        if (!album.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return RecordChange.Conflict;
        }

        context.Albums.Remove(album);
        return await PersistAsync(album, RecordChange.Conflict, cancellationToken);
    }

    public async Task<RecordChange> SaveArtistAsync(Artist artist, CancellationToken cancellationToken)
    {
        var name = artist.Name.Trim();
        var updating = artist.RowVersion.Length != 0;
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                // Keep the name availability check and write together even when upgrading duplicate names.
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                Artist target;
                if (!updating)
                {
                    target = new Artist();
                    context.Artists.Add(target);
                }
                else
                {
                    var existing = await context.Artists.SingleOrDefaultAsync(row => row.Id == artist.Id, cancellationToken);
                    if (existing is null)
                    {
                        return RecordChange.NotFound;
                    }

                    if (!existing.RowVersion.AsSpan().SequenceEqual(artist.RowVersion))
                    {
                        return RecordChange.Conflict;
                    }

                    target = existing;
                }

                if (target.Name != name && await context.Artists.AnyAsync(existing => (!updating || existing.Id != artist.Id) &&
                    EF.Functions.Collate(existing.Name, SearchCollation) == name, cancellationToken))
                {
                    context.Entry(target).State = EntityState.Detached;
                    return RecordChange.Duplicate;
                }

                target.Name = name;
                var result = await PersistAsync(target, RecordChange.Conflict, cancellationToken);
                if (result == RecordChange.Success)
                {
                    await transaction.CommitAsync(cancellationToken);
                    artist.Id = target.Id;
                    artist.RowVersion = target.RowVersion;
                }

                return result;
            });
        }
        catch (Exception exception) when (exception is SqlException { Number: 1205 } ||
            exception is DbUpdateException { InnerException: SqlException { Number: 1205 } })
        {
            return RecordChange.Conflict;
        }
    }

    public async Task<RecordChange> DeleteArtistAsync(int id, byte[] rowVersion, CancellationToken cancellationToken)
    {
        var artist = await context.Artists.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (artist is null)
        {
            return RecordChange.NotFound;
        }

        if (!artist.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return RecordChange.Conflict;
        }

        context.Artists.Remove(artist);
        return await PersistAsync(artist, RecordChange.ArtistInUse, cancellationToken);
    }

    private async Task<RecordChange> PersistAsync(object entity, RecordChange relationshipResult, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return RecordChange.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Entry(entity).State = EntityState.Detached;
            return RecordChange.Conflict;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 547 or 2601 or 2627 } sqlException)
        {
            context.Entry(entity).State = EntityState.Detached;
            return sqlException.Number == 547 ? relationshipResult : RecordChange.Duplicate;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
