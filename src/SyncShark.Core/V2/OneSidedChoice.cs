namespace Massdex.SyncShark.V2;

/// <summary>
/// A first-sync choice for files only on <paramref name="Present"/> under a pair-relative folder prefix ("" = whole pair):
/// remove them (recovery-first) or copy them to the other side (rule 32).
/// </summary>
public sealed record OneSidedChoice(string Prefix, Side Present, bool Remove);
