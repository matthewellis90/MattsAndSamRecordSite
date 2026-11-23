using System.Globalization;
using System.Text;
using MattsRecords.Models;
using Microsoft.EntityFrameworkCore;

namespace MattsRecords.Services;

internal sealed class PressingBatch(PressingLookup musicBrainz, DiscogsLookup discogs, IConfiguration configuration)
{
    public async Task RunAsync(RecordContext catalog, string reportPath, string? previousReport,
        CancellationToken cancellationToken, bool estimate = false)
    {
        var query = catalog.Albums.AsNoTracking().Include(record => record.Artist)
            .Where(record => record.Medium == RecordMedium.Vinyl);
        if (previousReport is not null && !estimate)
        {
            var failedIds = ReadFailedLookups(previousReport);
            query = query.Where(record => failedIds.Contains(record.Id));
        }
        Dictionary<int, List<PressingCandidate>> cached = estimate && previousReport is not null
            ? ReadPriorCandidates(previousReport) : [];
        var records = await query.OrderBy(record => record.Id).ToListAsync(cancellationToken);
        await using var report = new StreamWriter(reportPath);
        await report.WriteLineAsync("Record Room pressing review. A suggested link requires checking the physical copy.");
        var updated = 0;
        var review = 0;
        var estimated = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? error = null;
            PressingCandidate? mb = null, dg = null;
            IReadOnlyList<PressingCandidate> suggestions = [];
            var representative = record.PressingEstimated;
            if (NeedsDetails(record, estimate) && (!estimate || !record.PressingReviewed))
            {
                var prior = cached.TryGetValue(record.Id, out var savedCandidates)
                    ? savedCandidates.Where(candidate => Fits(record, candidate)).ToArray() : [];
                try
                {
                    if (prior.Length == 0 || HasSavedPressingEvidence(record))
                        (mb, suggestions) = await FindMusicBrainzAsync(record, cancellationToken, estimate);
                }
                catch (Exception exception) when (LookupUnavailable(exception, cancellationToken))
                {
                    error = "MusicBrainz unavailable (" + FailureReason(exception) + ")";
                }

                if (estimate && mb is null && !record.MusicBrainzId.HasValue && !record.DiscogsId.HasValue)
                {
                    var proposed = PreferCandidate(record, suggestions.Concat(prior));
                    if (proposed?.Id is Guid candidateId)
                    {
                        try
                        {
                            var detail = await musicBrainz.GetDetailsAsync(candidateId, cancellationToken);
                            mb = detail is not null && FitsRepresentative(record, detail) ? detail : null;
                        }
                        catch (Exception exception) when (LookupUnavailable(exception, cancellationToken))
                        {
                            mb = proposed;
                            error = "MusicBrainz unavailable (" + FailureReason(exception) + ")";
                        }
                        representative = mb is not null;
                    }
                }

                try
                {
                    dg = await FindDiscogsAsync(record, mb, cancellationToken, estimate);
                }
                catch (Exception exception) when (LookupUnavailable(exception, cancellationToken))
                {
                    var detail = "Discogs unavailable (" + FailureReason(exception) + ")";
                    error = error is null ? detail : error + ", " + detail;
                }
            }

            if (mb?.LinkedDiscogsId is int linked && record.DiscogsId is int saved && linked != saved)
            {
                (mb, dg) = (null, null);
                error = "Saved release identifiers disagree";
            }
            else if (mb is not null && dg is not null && !Compatible(mb, dg))
            {
                (mb, dg) = (null, null);
                error = "Catalog sources disagree about this pressing";
            }

            var current = await catalog.Albums.SingleOrDefaultAsync(item => item.Id == record.Id, cancellationToken);
            if (current is null || !current.RowVersion.SequenceEqual(record.RowVersion))
            {
                await report.WriteLineAsync($"{record.Id} | {record.DisplayArtist} — {record.Name} | Changed while looking up; rerun to review.");
                review++;
                catalog.ChangeTracker.Clear();
                continue;
            }

            if (!estimate)
                ApplyDetails(current, mb, dg);
            else if ((mb is not null || dg is not null) && !current.PressingReviewed)
            {
                ApplyPressingFields(current, mb, dg);
                if (representative && mb is not null)
                    current.PressingEstimated = true;
            }
            try
            {
                if (await catalog.SaveChangesAsync(cancellationToken) > 0)
                {
                    updated++;
                    if (current.PressingEstimated) estimated++;
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                await report.WriteLineAsync($"{record.Id} | {record.DisplayArtist} — {record.Name} | Changed while saving; rerun to review.");
                review++;
                catalog.ChangeTracker.Clear();
                continue;
            }

            if (current.PressingEstimated && mb?.Id is Guid estimateId)
                await report.WriteLineAsync($"{record.Id} | {record.DisplayArtist} — {record.Name} | Estimated edition https://musicbrainz.org/release/{estimateId}");

            if (NeedsDetails(current, estimate) || error is not null)
            {
                review++;
                var reason = error ?? (mb is null && dg is null ? "Check the sleeve or label for pressing identifiers." : "Some fields are still unavailable.");
                await report.WriteLineAsync($"{record.Id} | {record.DisplayArtist} — {record.Name} | {reason}");
                foreach (var candidate in suggestions.Take(3))
                    await report.WriteLineAsync($"  https://musicbrainz.org/release/{candidate.Id} | {candidate.Format} | {candidate.Label} | {candidate.CatalogueNumber} | {candidate.Country} | {candidate.Year}");
                var search = Uri.EscapeDataString($"{record.DisplayArtist} {record.Name}");
                await report.WriteLineAsync($"  https://www.discogs.com/search/?type=release&format=Vinyl&q={search}");
            }
            catalog.ChangeTracker.Clear();
            await report.FlushAsync(cancellationToken);
            Console.WriteLine($"Record {record.Id} checked. Updated {updated}; review {review}.");
        }
        Console.WriteLine($"Checked {records.Count} records. Updated {updated}; estimated updates {estimated}; incomplete or lookup issues {review}.");
        Console.WriteLine($"Review report: {reportPath}");
    }

    internal static int[] ReadFailedLookups(string previousReport)
    {
        var ids = new HashSet<int>();
        foreach (var line in File.ReadLines(previousReport))
        {
            var first = line.IndexOf(" | ", StringComparison.Ordinal);
            var last = line.LastIndexOf(" | ", StringComparison.Ordinal);
            if (first > 0 && last > first && int.TryParse(line.AsSpan(0, first), out var id) && id > 0 &&
                line.AsSpan(last + 3).StartsWith("MusicBrainz unavailable", StringComparison.Ordinal))
                ids.Add(id);
        }
        return ids.ToArray();
    }

    internal static Dictionary<int, List<PressingCandidate>> ReadPriorCandidates(string directory)
    {
        var candidates = new Dictionary<int, List<PressingCandidate>>();
        if (!Directory.Exists(directory))
            return candidates;

        var reports = Directory.EnumerateDirectories(directory, "The-Record-Room-Pressing-*")
            .SelectMany(folder => Directory.EnumerateFiles(folder, "Pressing *.txt"))
            .Where(path => Path.GetFileName(path).StartsWith("Pressing review - ", StringComparison.Ordinal) ||
                Path.GetFileName(path).StartsWith("Pressing retry - ", StringComparison.OrdinalIgnoreCase));
        foreach (var report in reports)
        {
            var recordId = 0;
            string artist = "", title = "";
            foreach (var line in File.ReadLines(report))
            {
                var fields = line.Split(" | ");
                if (fields.Length >= 3 && int.TryParse(fields[0], out var parsedId))
                {
                    recordId = parsedId;
                    var identity = fields[1].Split(" — ", 2);
                    (artist, title) = identity.Length == 2 ? (identity[0], identity[1]) : ("", "");
                    continue;
                }

                if (fields.Length != 6 || string.IsNullOrEmpty(title) ||
                    !fields[0].StartsWith("  https://musicbrainz.org/release/", StringComparison.Ordinal) ||
                    !Guid.TryParse(fields[0].Split('/').Last(), out var releaseId))
                    continue;
                var year = int.TryParse(fields[5], out var value) ? (int?)value : null;
                if (!candidates.TryGetValue(recordId, out var list))
                    candidates[recordId] = list = [];
                list.Add(new PressingCandidate(title, artist, fields[3], fields[2], "", fields[4], year, fields[1],
                    "", releaseId, null, null, "", "", false));
            }
        }
        return candidates;
    }

    private async Task<(PressingCandidate? Match, IReadOnlyList<PressingCandidate> Suggestions)> FindMusicBrainzAsync(
        Album record, CancellationToken cancellationToken, bool estimate)
    {
        if (record.MusicBrainzId is Guid known)
        {
            var found = await musicBrainz.GetDetailsAsync(known, cancellationToken);
            return (found is not null && Fits(record, found) ? found : null, []);
        }

        var (field, term) = !string.IsNullOrWhiteSpace(record.CatalogNumber)
            ? ("Catalogue", record.CatalogNumber) : ValidBarcode(record.Barcode)
                ? ("Barcode", record.Barcode!) : ("Title", record.Name);
        var candidates = await musicBrainz.FindAsync(field, term, record.DisplayArtist, cancellationToken,
            requireComplete: field != "Title", broadVinylSearch: estimate && field == "Title");
        var plausible = candidates.Where(candidate => Fits(record, candidate)).ToArray();
        var proven = plausible.Where(candidate => HasPressingEvidence(record, candidate)).ToArray();
        if (proven is [{ Id: Guid id }])
        {
            var detailed = await musicBrainz.GetDetailsAsync(id, cancellationToken);
            if (detailed is not null && Fits(record, detailed) && HasPressingEvidence(record, detailed))
                return (detailed, plausible);
        }
        return (null, plausible);
    }

    private async Task<PressingCandidate?> FindDiscogsAsync(Album record, PressingCandidate? mb, CancellationToken cancellationToken,
        bool estimate)
    {
        var id = record.DiscogsId ?? mb?.LinkedDiscogsId;
        var known = id.HasValue;
        if (!known && !string.IsNullOrWhiteSpace(configuration["Discogs:Token"]))
        {
            var catalogue = string.IsNullOrWhiteSpace(record.CatalogNumber) && estimate ? mb?.CatalogueNumber : record.CatalogNumber;
            var (field, term) = !string.IsNullOrWhiteSpace(catalogue)
                ? ("catno", catalogue) : ("barcode", ValidBarcode(record.Barcode) ? record.Barcode : null);
            if (term is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
                var matches = await discogs.FindVinylIdsAsync(field, term, record.Name, record.DisplayArtist, cancellationToken);
                if (matches is [var only])
                    id = only;
            }
        }
        if (id is not int releaseId)
            return null;

        await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
        var found = await discogs.GetDetailsAsync(releaseId, ValidBarcode(record.Barcode) ? record.Barcode : null, cancellationToken);
        return found is not null && Fits(record, found) && (known || HasPressingEvidence(record, found) ||
            (estimate && mb is not null && Same(mb.CatalogueNumber, found.CatalogueNumber) &&
             Same(CountryName(mb.Country), CountryName(found.Country)) && Compatible(mb, found))) ? found : null;
    }

    private static bool HasSavedPressingEvidence(Album record) => record.MusicBrainzId.HasValue ||
        record.DiscogsId.HasValue || ValidBarcode(record.Barcode) || !string.IsNullOrWhiteSpace(record.CatalogNumber);

    internal static PressingCandidate? PreferCandidate(Album record, IEnumerable<PressingCandidate> sources) =>
        sources.Where(candidate => FitsRepresentative(record, candidate))
            .OrderByDescending(candidate => candidate.Year is >= 1000 && record.Year is >= 1000 && candidate.Year == record.Year)
            .ThenByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.CatalogueNumber) && !string.IsNullOrWhiteSpace(candidate.Label))
            .ThenByDescending(candidate => candidate.Year.HasValue && !string.IsNullOrWhiteSpace(candidate.Country))
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();

    private static bool FitsRepresentative(Album record, PressingCandidate candidate) => candidate.Id.HasValue &&
        (candidate.Kind.Length == 0 || Same(candidate.Kind, record.Kind.ToString())) && Fits(record, candidate) &&
        (string.IsNullOrWhiteSpace(record.CatalogNumber) || Same(record.CatalogNumber, candidate.CatalogueNumber)) &&
        (!ValidBarcode(record.Barcode) || ValidBarcode(candidate.Barcode));

    private static bool NeedsDetails(Album record, bool estimate) =>
        (!estimate && (!Album.IsDetailedVinylFormat(record.Format) || string.IsNullOrWhiteSpace(record.Genre))) ||
        string.IsNullOrWhiteSpace(record.Label) || string.IsNullOrWhiteSpace(record.CatalogNumber) ||
        string.IsNullOrWhiteSpace(record.Country) ||
        !record.PressingYear.HasValue || !record.MusicBrainzId.HasValue || !record.DiscogsId.HasValue;

    private static bool LookupUnavailable(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string FailureReason(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        OperationCanceledException => "timeout",
        _ => "connection or response error"
    };

    internal static bool Fits(Album record, PressingCandidate candidate) =>
        Same(record.Name, candidate.Title) && SameArtist(record.DisplayArtist, candidate.Artist) &&
        candidate.Format.Split(" / ").All(format => format.Contains("vinyl", StringComparison.OrdinalIgnoreCase)) &&
        CompatibleText(record.CatalogNumber, candidate.CatalogueNumber) &&
        CompatibleText(CountryName(record.Country), CountryName(candidate.Country)) &&
        CompatibleText(record.Label, candidate.Label) &&
        CompatibleBarcode(record.Barcode, candidate.Barcode) &&
        (!record.PressingYear.HasValue || !candidate.Year.HasValue || record.PressingYear == candidate.Year) &&
        (!Album.IsDetailedVinylFormat(record.Format) || !Album.IsDetailedVinylFormat(candidate.Format) ||
            Same(record.Format, candidate.Format));

    internal static bool HasPressingEvidence(Album record, PressingCandidate candidate)
    {
        var sameBarcode = RecordBarcode.EquivalentCodes(record.Barcode ?? "")
            .Intersect(RecordBarcode.EquivalentCodes(candidate.Barcode)).Any();
        var sameCatalogue = Same(record.CatalogNumber, candidate.CatalogueNumber);
        var sameCountry = Same(CountryName(record.Country), CountryName(candidate.Country));
        var sameYear = record.PressingYear.HasValue && record.PressingYear == candidate.Year;
        return (sameBarcode && (sameCatalogue || (sameCountry && sameYear))) ||
            (sameCatalogue && Same(record.Label, candidate.Label) && (sameCountry || sameYear));
    }

    private static bool Compatible(PressingCandidate left, PressingCandidate right) =>
        CompatibleText(left.CatalogueNumber, right.CatalogueNumber) && CompatibleText(left.Label, right.Label) &&
        CompatibleText(CountryName(left.Country), CountryName(right.Country)) &&
        CompatibleBarcode(left.Barcode, right.Barcode) &&
        (!left.Year.HasValue || !right.Year.HasValue || left.Year == right.Year) &&
        (!Album.IsDetailedVinylFormat(left.Format) || !Album.IsDetailedVinylFormat(right.Format) || Same(left.Format, right.Format));

    internal static void ApplyDetails(Album record, PressingCandidate? mb, PressingCandidate? dg)
    {
        if (!Album.IsDetailedVinylFormat(record.Format))
        {
            var format = Album.IsDetailedVinylFormat(mb?.Format) ? mb!.Format : dg?.Format;
            record.Format = Fill(null, format, 100) ?? Album.VinylFormat;
        }
        ApplyPressingFields(record, mb, dg);
        record.Barcode = Fill(record.Barcode, mb?.Barcode, 32, dg?.Barcode);
        record.Genre = Fill(record.Genre, mb?.Genre, 80, dg?.Genre);
        if (record.Year is null or 0)
            record.Year = mb?.ReleaseYear is >= 1000 ? mb.ReleaseYear : dg?.ReleaseYear is >= 1000 ? dg.ReleaseYear : record.Year;
        record.SpotifyUrl ??= mb?.SpotifyUrl;
    }

    internal static void ApplyPressingFields(Album record, PressingCandidate? mb, PressingCandidate? dg)
    {
        record.Label = Fill(record.Label, mb?.Label, 100, dg?.Label);
        record.CatalogNumber = Fill(record.CatalogNumber, mb?.CatalogueNumber, 100, dg?.CatalogueNumber);
        record.Country = Fill(record.Country, dg?.Country, 80, CountryName(mb?.Country));
        record.PressingYear ??= mb?.Year is >= 1000 ? mb.Year : dg?.Year is >= 1000 ? dg.Year : null;
        record.MusicBrainzId ??= mb?.Id;
        record.DiscogsId ??= dg?.DiscogsId;
    }

    private static string? Fill(string? current, string? preferred, int limit, string? alternate = null) =>
        !string.IsNullOrWhiteSpace(current) ? current :
        new[] { preferred, alternate }.Select(value => value?.Trim()).FirstOrDefault(value =>
            value is { Length: > 0 } && value.Length <= limit && value is not ("[none]" or "[no label]")) ?? current;

    private static bool ValidBarcode(string? value) => RecordBarcode.EquivalentCodes(value ?? "").Length > 0;

    private static bool CompatibleBarcode(string? left, string? right) => !ValidBarcode(left) || !ValidBarcode(right) ||
        RecordBarcode.EquivalentCodes(left!).Intersect(RecordBarcode.EquivalentCodes(right!)).Any();

    private static bool CompatibleText(string? left, string? right) => string.IsNullOrWhiteSpace(left) ||
        string.IsNullOrWhiteSpace(right) || Same(left, right);

    private static bool Same(string? left, string? right) => Key(left) is { Length: > 0 } key && key == Key(right);

    private static bool SameArtist(string left, string right) =>
        (left == "Uncredited" && string.IsNullOrWhiteSpace(right)) || Same(WithoutArticle(left), WithoutArticle(right));

    private static string WithoutArticle(string name) =>
        name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    private static string Key(string? value) => string.Concat((value ?? "").Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static string? CountryName(string? country)
    {
        if (country is "UK" or "GB") return "United Kingdom";
        if (country == "US") return "United States";
        if (country?.Length != 2) return country;
        try { return new RegionInfo(country).EnglishName; }
        catch (ArgumentException) { return country; }
    }
}
