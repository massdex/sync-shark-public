namespace Massdex.SyncShark.V2;

/// <summary>Independent self/ancestor and self/descendant answers for one requested path.</summary>
public readonly record struct ProtectionMatch(bool Covered, bool Contains);
