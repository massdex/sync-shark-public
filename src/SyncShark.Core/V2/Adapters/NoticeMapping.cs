namespace Massdex.SyncShark.V2;

/// <summary>Projects v2 notices onto the window's conflict records.</summary>
internal static class NoticeMapping
{
    /// <summary>One open notice as the conflicts page shows it.</summary>
    internal static SyncConflictComparison ToComparison(Notice notice) => new(
        ConflictId: notice.NoticeId ?? string.Empty,
        PairId: notice.PairId,
        RelativePath: notice.Path,
        Generation: 0,
        LocalSize: null,
        DropboxSize: null,
        LocalHash: null,
        DropboxHash: null,
        LocalModifiedUtc: null,
        DropboxModifiedUtc: null,
        Group: GroupLabel(notice.Kind),
        Recommendation: notice.Detail,
        Status: "open",
        Options: NoticeChoices.Options(notice));

    /// <summary>One open notice as the page's selection snapshot.</summary>
    internal static SyncConflictSelection ToSelection(Notice notice) =>
        new(notice.NoticeId ?? string.Empty, notice.PairId, notice.Path, 0);

    /// <summary>The screen's group label for one notice kind.</summary>
    internal static string GroupLabel(NoticeKind kind) => kind switch
    {
        NoticeKind.Info => "Information",
        NoticeKind.KeepBoth => "Kept both",
        NoticeKind.EditBeatsDelete => "Edit kept over delete",
        NoticeKind.AutoConflict => "Decided automatically",
        NoticeKind.UserDecision => "Needs your decision",
        _ => throw new InvalidDataException("Unknown notice kind.")
    };
}
