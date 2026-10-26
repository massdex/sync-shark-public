namespace Massdex.SyncShark.V2;

/// <summary>The latest in-memory progress of one pair's automatic or reconciliation work.</summary>
public sealed record LiveProgress(string Kind, string Phase, long Done, long Total, string? Path, TimeSpan? Eta,
    DateTimeOffset UpdatedUtc)
{
    internal const string AutoSyncKind = "autosync";
    internal const string ReconciliationKind = "reconciliation";
}
