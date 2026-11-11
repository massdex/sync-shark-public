namespace Massdex.SyncShark.Engine;

/// <summary>One atomic ancestor classification, with the unbound folder to join when applicable.</summary>
public readonly record struct AncestorAnswer(AncestorNews News, string? UnboundKey = null);
