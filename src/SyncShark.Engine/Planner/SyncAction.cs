namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Row decision;</summary>
public sealed record SyncAction(ActionKind Kind, Row Row, NoneReason None = NoneReason.InSync, NoticeKind NoticeKind = NoticeKind.Info, TextKey Cause = default, string? Target = null, TextKey? InfoAfter = null, Side? Side = null)
{
    /// <summary>WrittenSide operation on the supplied facts.</summary>
    public Side WrittenSide() => throw new NotImplementedException();
    /// <summary>WritesLocal operation on the supplied facts.</summary>
    public bool WritesLocal() => throw new NotImplementedException();
    /// <summary>CreatesNewItem operation on the supplied facts.</summary>
    public bool CreatesNewItem() => throw new NotImplementedException();
    /// <summary>IsInSync operation on the supplied facts.</summary>
    public bool IsInSync() => throw new NotImplementedException();
}

/// <summary>Why a row needs no effect (section 6.1). Unfinished: a temporary, proven no-effect write left its work
/// retained for a timed retry; it is not success.</summary>
public enum NoneReason { InSync, OutOfScope, Skipped, Held, RemovedWithFolder, Unfinished }
