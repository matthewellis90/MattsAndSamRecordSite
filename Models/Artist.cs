namespace MattsRecords.Models;

public sealed class Artist
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
}
