using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Rename outcome: MovedSide identifies the moved side; Target names the follow/recovery destination.
/// NoticeKind/Cause describe a notice or split Info.</summary>
public sealed record RenameDecision(RenameKind Kind, Side? MovedSide = null, string? Target = null,
    IssueKind? NoticeKind = null, TextKey? Cause = null)
{
    /// <summary>The side this decision writes: Dropbox for a finished case rename, else the moved side's counterpart;
    /// null for every other kind and for a malformed Follow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Side? WrittenSide() => Kind switch
    {
        RenameKind.FinishCaseRename => Engine.Side.Dropbox,
        RenameKind.Follow => MovedSide is { } moved ? PathNames.Other(moved) : null,
        _ => null
    };
}
