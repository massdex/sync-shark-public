namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Row decision: Target is the destination, Side selects the affected endpoint and InfoAfter names the notice after success.</summary>
public sealed record SyncAction(ActionKind Kind, Row Row, NoneReason None = NoneReason.InSync, NoticeKind NoticeKind = NoticeKind.Info, TextKey Cause = default, string? Target = null, TextKey? InfoAfter = null, Side? Side = null)
{
    /// <summary>The copy or folder-create kind for the written side.</summary>
    public static ActionKind CopyOn(bool folder, Side written) => throw new NotImplementedException();
    /// <summary>The delete kind for the selected side.</summary>
    public static ActionKind DeleteOn(Side side) => throw new NotImplementedException();
    /// <summary>The side this action writes.</summary>
    public Side WrittenSide() => throw new NotImplementedException();
    /// <summary>Whether the effect writes or removes local content.</summary>
    public bool WritesLocal() => throw new NotImplementedException();
    /// <summary>Whether this creates rather than replaces an existing item.</summary>
    public bool CreatesNewItem() => throw new NotImplementedException();
    /// <summary>Whether both endpoints already match.</summary>
    public bool IsInSync() => throw new NotImplementedException();
}

/// <summary>Why a row needs no effect (section 6.1). Unfinished: a temporary, proven no-effect write left its work
/// retained for a timed retry; it is not success.</summary>
public enum NoneReason { InSync, OutOfScope, Skipped, Held, RemovedWithFolder, Unfinished }
