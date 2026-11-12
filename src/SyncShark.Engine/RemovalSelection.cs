namespace Massdex.SyncShark.Engine;

/// <summary>The Executor's selected losing proof together with the exact saved binding it was selected against,
/// conveyed to the Local removal arm boundary. Saved is null only when the selection observed no saved binding,
/// never as "not supplied"; Local copies it unchanged into the pending note.</summary>
public sealed record RemovalSelection(RemovalProof Proof, SyncedEntry? Saved);
