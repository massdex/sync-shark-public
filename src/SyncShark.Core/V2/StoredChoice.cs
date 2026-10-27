namespace Massdex.SyncShark.V2;

/// <summary>One reviewed one-sided choice and the effect the prepare reported for it.</summary>
internal sealed record StoredChoice(string Side, string Prefix, string Action, int Files, int Folders, long Bytes);
