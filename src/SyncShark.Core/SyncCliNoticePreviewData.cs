namespace Massdex.SyncShark;

/// <summary>The exact notice `conflicts resolve` names, read as it stands now before any choice applies, with
/// the chosen choice's own description and the confirmed command that would apply it. Notice is null when no
/// notice currently has that key, and ConfirmCommand is null with it.</summary>
public sealed record SyncCliNoticePreviewData(string PairId, string Key, string Choice,
    SyncCliConflictData? Notice, string? ChoiceEffect, string? ConfirmCommand);
