using MattsRecords.Services;

namespace MattsRecords.Models;

public sealed record SharedCatalogView(Guid Token, string Username, string? Search, RecordPage Results,
    int CollectionSize, IReadOnlyList<Album> Recent, RecordMedium? Medium = null);
public sealed record SharedRecordView(Guid Token, Album Record);
