namespace Massdex.SyncShark.V2;

public sealed record DateRepairExample(string Path, DateTimeOffset OldUtc, DateTimeOffset NewUtc, string Outcome)
{
    public string? Evidence { get; init; }
    public string? Hash { get; init; }
    public string? ObjectId { get; init; }
    public string? ObjectKey { get; init; }
    public long Size { get; init; }
}
