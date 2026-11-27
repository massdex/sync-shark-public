namespace Massdex.SyncShark.Engine;

/// <summary>Pair-scoped durable facts and streamed Repair rows. Source boundaries advance only on settlement.</summary>
public interface IStore : IDisposable
{
    IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct);
    /// <summary>Host admission only: stream pairs and raise one failure notice per malformed pair. Queries use PairsAsync, which changes no state.</summary>
    IAsyncEnumerable<PairConfig> DiagnosedPairsAsync(CancellationToken ct) => PairsAsync(ct);
    Task<PairConfig?> PairAsync(string pairId, CancellationToken ct);
    /// <summary>Receives the committed pair, derived keep legs included, after each pair-config and root-settlement commit.</summary>
    Action<PairConfig>? PairChanged { get => null; set { } }
    /// <summary>Receives the pair id after a committed binding install owed its file's other names; it runs under the store's writer gate and must not call the store.</summary>
    Action<string>? ScopesOwed { get => null; set { } }
    /// <summary>Relink a proved kept move, including inherited departures and folder descendants, as one publication.</summary>
    Task FollowKeepAsync(string pairId,SyncedEntry before,SyncedEntry after,Side? departure,CancellationToken ct);
    /// <summary>Enroll the currently stored raw rules, not the admission snapshot.</summary>
    Task<PairConfig> EnrollKeepsAsync(PairConfig pair, CancellationToken ct) => Task.FromResult(pair);
    /// <summary>Publishes configuration and each distinct returned footprint at its owner in one transaction, advancing the existing Repair revision even if the same scope was already owed.</summary>
    Task SavePairAsync(PairConfig pair, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null, Exclusion? selected = null);
    /// <summary>Return the exact refusal cause from the publication transaction (accepted job, armed PC note, or PutBack notice, including ignored). Null means the pair was removed and its scopes returned atomically; refusal changes neither. An unknown pair returns PairUnknown.</summary>
    Task<TextKey?> RemovePairAsync(string pairId, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null);
    /// <summary>Binding projections include the saved PC publication journal and boundary; only the dirty owner compares them.</summary>
    Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct);
    IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct);
    IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct);
    /// <summary>Commit the record change and spend the exact acknowledged historical deletion key atomically, even when neither side has a previous binding. Without a new publication keep the carried boundary only on a same-PC-identity relink.</summary>
    Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, CancellationToken ct, DateTimeOffset? spentDeletedAt = null, (IReadOnlyList<Notice> Tokens,Func<bool> Free)? closing = null);
    IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct);
    /// <summary>Save by pair, path and kind with the members. An unrelated Failure cannot replace a PutBack notice. Within the same PutBack slot, the newest put-back failure at that path replaces its predecessor. Returns the header as stored, or null when the store refused it.</summary>
    Task<Notice?> SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct, Notice? replacing = null);
    /// <summary>Change visibility only if the stored notice still matches the reviewed subject; return false on a stale or closed slot.</summary>
    Task<bool> UpdateNoticeVisibilityAsync(string pairId, Notice expected, bool ignored, CancellationToken ct) => throw new NotImplementedException();
    Task<bool> CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct, Func<bool>? free = null);
    Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct);
    Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct);
    Task<string?> CursorAsync(string pairId, CancellationToken ct);
    Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct);
    /// <summary>Save the settled change cursor and append its page's deleted names in the same transaction.</summary>
    Task SaveCursorAsync(string pairId, string cursor, IReadOnlyList<string> deletedKeys, CancellationToken ct) =>
        deletedKeys.Count == 0 ? SaveCursorAsync(pairId, cursor, ct) : throw new NotImplementedException();
    Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct);
    /// <summary>Owe one scoped Repair at the stored pair in one transaction.</summary>
    Task OweScopeAsync(string pairId, Scope scope, CancellationToken ct);
    /// <summary>Read the existing source revision and whole-pair reason before acquisition.</summary>
    Task<(long Revision, string? Reason)> ReadRepairAsync(string pairId, Side side, CancellationToken ct);
    /// <summary>Read the Dropbox revision net of owed-scope advances, with its reason, in one coherent read; only the full finish matches it.</summary>
    Task<(long Revision, string? Reason)> ReadFullRepairAsync(string pairId, CancellationToken ct);
    /// <summary>The completion ending the entire two-sided Repair always publishes its required summary and, in the same transaction, clears the reason of each side whose captured revision still matches, the Dropbox one net of owed-scope advances; a side that moved stays owed. first_repair_done needs both sides. Returns which sides matched.</summary>
    Task<(bool Local,bool Dropbox)> FinishRepairAsync(string pairId, (long Local,long Dropbox) revisions, string completedRepairJson, CancellationToken ct);
    /// <summary>Retire only the matching mapped scope at the owner’s Dropbox revision. No first-run bit or full summary is published.</summary>
    Task<bool> FinishRepairAsync(string pairId, Scope scope, long revision, CancellationToken ct);
    Task SaveJobAsync(AcceptedJob job, CancellationToken ct);
    IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct);
    Task SettleJobAsync(AcceptedJob job, CancellationToken ct);
    Task<bool> ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct);
    Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct);
    Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct);
    /// <summary>Capture names only for the Dropbox history acquisition; read its terminal cursor only after the listing finishes successfully.</summary>
    Task AddScanAsync(string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, Func<string>? terminalHistoryCursor, CancellationToken ct);
    /// <summary>Project the notice and cause for each kind at the path. No compare outcome settles PutBack: only its own Retry or a later change of that same item can.</summary>
    IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct);
    Task ClearScanAsync(string pairId, Guid scanId, CancellationToken ct);

    /// <summary>Read the persisted full-first-Repair bit and last summary without inferring them from record count.</summary>
    Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(string pairId, CancellationToken ct);

    /// <summary>Read deleted-name publication state independently of the change-feed cursor.</summary>
    Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, CancellationToken ct);
    /// <summary>Read history state for the active scan after verifying the saved history.</summary>
    Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.</summary>
    Task PublishDeletedHistoryAsync(string pairId, Guid scanId, CancellationToken ct);

    /// <summary>A spent delta without a recorded deletion time masks the name in both the current acquisition and published history.</summary>
    Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, CancellationToken ct);

    /// <summary>Read the Dropbox deletion time this exact name already consumed; null when its spend recorded none.</summary>
    Task<DateTimeOffset?> ConsumedDeletionAsync(string pairId, string key, CancellationToken ct);

    /// <summary>Project both physical endpoints of a settled folder move set-wise in one transaction.</summary>
    Task ProjectSubtreeAsync(string pairId, Scope from, Scope to, CancellationToken ct);

    /// <summary>Indexed exact-path notice lookup; no whole-pair notice scan on the item path.</summary>
    IAsyncEnumerable<Notice> NoticesAtAsync(string pairId, string pathKey, CancellationToken ct);
    /// <summary>On lost journal continuity only, install the fresh volume boundary and increment the Local Repair revision/reason of every durably bound member of that volume in one transaction; select members at commit, not from the caller snapshot.</summary>
    Task ResetJournalAsync(string volume, JournalPosition fresh, string reason, CancellationToken ct);
    /// <summary>On Dropbox reset only, install its fresh cursor and increment that pair’s Dropbox Repair revision/reason in one transaction.</summary>
    Task ResetCursorAsync(string pairId, string fresh, string reason, CancellationToken ct);
    /// <summary>Stream the existing scan’s move candidates before ordinary differences, ordered by shallowest candidate of the binding then by binding so all its observed sides are adjacent.</summary>
    IAsyncEnumerable<RenameCandidate> RenamesAsync(string pairId, Guid scanId, Scope? scope, CancellationToken ct);
    /// <summary>Replace the old link with both new links and their notice in one transaction.</summary>
    Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry second, Notice notice, CancellationToken ct);
    /// <summary>In one transaction match the reviewed drive notice and publish the chosen pair; only FirstSync resets bindings and first Repair.</summary>
    Task<bool> ResolveDriveAsync(PairConfig pair, Notice reviewed, ResolutionChoice choice, CancellationToken ct);
    /// <summary>Stream notices whose members overlap the mapped footprint on their own side, including covering ancestors.</summary>
    IAsyncEnumerable<Notice> NoticesUnderAsync(string pairId, Scope footprint, CancellationToken ct);
    /// <summary>Add or retire only this observed link member; close the pair-wide link notice only when no members remain.</summary>
    Task UpdateLinkMemberAsync(string pairId, NoticeItem member, bool present, CancellationToken ct);
    /// <summary>Read one indexed notice by pair and ID; the caller must still validate its reviewed token.</summary>
    Task<Notice?> NoticeAsync(string pairId, long id, CancellationToken ct);
    /// <summary>Publish the single receiving-side keep-both link and its notice atomically.</summary>
    Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, Notice notice, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read unspent history for a file or folder candidate.</summary>
    Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, bool isFolder, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Register the active acquisition before its boundaries are read.</summary>
    void BeginScan(string pairId, Guid scanId) => throw new NotImplementedException();
    /// <summary>Check whether a concurrent write touched this footprint.</summary>
    bool ScanTouched(string pairId, Guid scanId, Side side, string path, bool descendants) => throw new NotImplementedException();
}
