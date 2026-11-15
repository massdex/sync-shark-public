using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark.Engine;

/// <summary>Facts about a row's surroundings, gathered by Run (or the Observer); Destination is filled by the Executor
/// before deciding (section 7.1 step 0): for a row missing one side, that side's path. HoldsNestedRoot: on the
/// UnderFolderDelete side, another pair's root is at or under this row's path (D12; FolderChildFacts for step 4).
/// HeldKind: the kind of the notice holding the row (2.4), so a hold refreshes only into its own kind.
/// CompleteInventory: the current acquisition is complete, so P4 discovery membership has authority; DeletionEligible:
/// P4 selected this row for investigation or folder finalization, never deletion authority. FirstRunDeletion carries
/// identity-validated retained disposition; its persisted folder context is not authority for a later acquisition.
/// The Executor supplies current comparison time to TimeRules (P6 phase 1); no clock is carried here.</summary>
public sealed record RowContext(bool OutOfScope, bool Skipped, bool Held, Side? UnderFolderDelete,
    bool HoldsNestedRoot, bool FirstRun, bool CompleteInventory, bool DeletionEligible,
    string? Destination = null, IssueKind? HeldKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    IssueSubject.FirstRunDeletionKind FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None)
{
    /// <summary>The row facts no observation has added yet; one shared immutable instance for every caller that
    /// observes before projecting.</summary>
    public static RowContext Neutral { get; } = new(false, false, false, null, false, false, false, false);

    /// <summary>Evaluates one row's context from facts Run already acquired. The ordinary path is inlined and
    /// returns neutral itself; only an exceptional row allocates a context, copying FirstRun and CompleteInventory.
    /// neutral must carry no row facts: OutOfScope/Skipped/Held/HoldsNestedRoot/DeletionEligible false,
    /// UnderFolderDelete/HeldKind/Destination null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RowContext ForRow(Row row, PathScope scope, RowContext neutral, bool skipped,
        IssueKind? heldKind, Side? underFolderDelete, bool deletionEligible,
        IssueSubject.FirstRunDeletionKind firstRunDeletion)
    {
        Debug.Assert(neutral is { OutOfScope: false, Skipped: false, Held: false, UnderFolderDelete: null,
            HoldsNestedRoot: false, DeletionEligible: false, Destination: null, HeldKind: null,
            FirstRunDeletion: IssueSubject.FirstRunDeletionKind.None });
        // Scope evaluates the listed path, else the saved path, on each side (the corrected exclusion rule).
        var outOfScope = scope.Excludes(row.Local?.Path ?? row.Synced?.LocalPath,
            row.Dropbox?.Path ?? row.Synced?.DropboxPath,
            row.IsFolder());
        if (!outOfScope && !skipped && heldKind is null && underFolderDelete is null && !deletionEligible &&
            firstRunDeletion == IssueSubject.FirstRunDeletionKind.None)
            return neutral;
        return Exceptional(row, scope, neutral, outOfScope, skipped, heldKind, underFolderDelete, deletionEligible,
            firstRunDeletion);
    }

    // Kept out of ForRow so the ordinary path stays small enough to inline; nested-root scope is only asked here.
    private static RowContext Exceptional(Row row, PathScope scope, RowContext neutral, bool outOfScope,
        bool skipped, IssueKind? heldKind, Side? underFolderDelete, bool deletionEligible,
        IssueSubject.FirstRunDeletionKind firstRunDeletion) =>
        new(OutOfScope: outOfScope,
            Skipped: skipped,
            Held: heldKind is not null,
            UnderFolderDelete: underFolderDelete,
            HoldsNestedRoot: underFolderDelete is { } side &&
                (side == Side.Local ? row.Local?.Path ?? row.Synced?.LocalPath
                    : row.Dropbox?.Path ?? row.Synced?.DropboxPath) is { } path &&
                scope.HoldsNestedRoot(side, path),
            FirstRun: neutral.FirstRun,
            CompleteInventory: neutral.CompleteInventory,
            DeletionEligible: deletionEligible,
            HeldKind: heldKind,
            FirstRunDeletion: firstRunDeletion);
}
