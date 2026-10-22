namespace Massdex.SyncShark.V2;

/// <summary>
/// One notice reason: the stable key code matches on and the display text shown to the user. AwaitsAcknowledgement
/// marks a record of an automatic decision: Run never closes it, only the user does (notices design §1).
/// </summary>
public sealed record NoticeReason(string Key, string Detail, bool AwaitsAcknowledgement = false);
