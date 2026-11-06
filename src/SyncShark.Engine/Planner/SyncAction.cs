using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Row decision; Target is the destination, Side selects the side for ReplaceKind/MoveUnlinked,
/// and InfoAfter is the Info notice emitted after success.</summary>
public sealed record SyncAction(ActionKind Kind, Row Row, NoneReason None = NoneReason.InSync,
    IssueKind NoticeKind = IssueKind.Info, TextKey Cause = default, string? Target = null,
    TextKey? InfoAfter = null, Side? Side = null)
{
    /// <summary>The side this action writes; a missing explicit side or a state-only kind is a caller error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Side WrittenSide()
    {
        var action = this;
        return Kind switch
        {
            ActionKind.Upload or ActionKind.EnsureDropboxFolder or ActionKind.DeleteDropbox or ActionKind.MoveDropbox
                => Engine.Side.Dropbox,
            ActionKind.Download or ActionKind.EnsureLocalFolder or ActionKind.DeleteLocal or ActionKind.MoveLocal
                => Engine.Side.Local,
            ActionKind.ReplaceKind or ActionKind.MoveUnlinked => action.Side ?? throw new ArgumentException(nameof(action.Side)),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    /// <summary>A new-item create intent, not an overwrite or a restore of a saved item.</summary>
    public bool CreatesNewItem() =>
        Kind is (ActionKind.Upload or ActionKind.Download or ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder) &&
        Row.Synced is null && Row.EntryOn(WrittenSide()) is null;

    /// <summary>A settled no-op: nothing to do because both sides already match.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInSync() => Kind == ActionKind.None && None == NoneReason.InSync;

    /// <summary>The copy kind that fetches a folder or file onto the written side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ActionKind CopyOn(bool folder, Side written) => folder
        ? written == Engine.Side.Local ? ActionKind.EnsureLocalFolder : ActionKind.EnsureDropboxFolder
        : written == Engine.Side.Local ? ActionKind.Download : ActionKind.Upload;

    /// <summary>The delete kind for a side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ActionKind DeleteOn(Side side) =>
        side == Engine.Side.Local ? ActionKind.DeleteLocal : ActionKind.DeleteDropbox;
}
