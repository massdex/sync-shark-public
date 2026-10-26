namespace Massdex.SyncShark.V2;

public sealed record DateRepairReport(string PairId, bool DryRun, long Examined, long Eligible, long Changed,
    long Skipped, string ReportFile, IReadOnlyList<DateRepairExample> Examples)
{
    public long MissingWriteHistory { get; init; }
    public long LocalDateNotWritten { get; init; }
    public long Mismatches { get; init; }
    public string? CsvFile { get; init; }
}
