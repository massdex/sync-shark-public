using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark.Engine;

/// <summary>What one Syncer entry point did. Cause is the raised notice's cause.</summary>
public sealed record SyncOutcome(ActionKind Kind, NoneReason? None, EffectKind? Effect, IssueKind? Raised,
    // Cause steers holds inside a run; the resolve JSON keeps its published shape.
    bool Deferred, [property: JsonIgnore] TextKey? Cause = null)
{
    /// <summary>The immutable, row-independent successful no-op result.</summary>
    public static readonly SyncOutcome InSync = new(ActionKind.None, NoneReason.InSync, null, null, false);

    /// <summary>Whether this outcome completed without deferred or blocking responsibility.</summary>
    public bool Succeeded() => !Deferred && !RaisedBlocks() && (None is null or NoneReason.InSync);

    /// <summary>True when the raised kind blocks further work on the item; Deferred and None remain each caller's
    /// own conditions (2.4, 7.9).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RaisedBlocks() => Raised is IssueKind.Failure or IssueKind.Tie or IssueKind.NameClash;

    /// <summary>Whether this outcome still owes work for its row (8.1), or null when it gives no answer at all: a
    /// deferred or removed-with-folder outcome is ignored, so a caller must not clear an earlier responsibility.
    /// False is a real answer that completes the row. The store is asked only once those and the scope and skip
    /// gates admit the row.</summary>
    internal bool? OwesWork(Row row, IStateStore store, string pairId) =>
        Deferred || None is NoneReason.RemovedWithFolder ? null :
        !Succeeded() && (None is not NoneReason.OutOfScope || !row.Context.OutOfScope) &&
        (!row.Context.Skipped || store.UnreadableTouches(pairId, row.Key, true));

    /// <summary>True when this handled failure of a local folder replaced by a file must hold the row's dependent
    /// children; the action actually attempted is judged, not the scheduled row.</summary>
    internal bool BlocksDependentRows(ActionKind attemptedKind, Side? attemptedSide, Row effectiveRow) =>
        RaisedBlocks()
        && attemptedKind == ActionKind.ReplaceKind
        && attemptedSide == Side.Local
        && effectiveRow.Local is { IsFolder: true }
        && effectiveRow.Dropbox is { IsFolder: false };
}
