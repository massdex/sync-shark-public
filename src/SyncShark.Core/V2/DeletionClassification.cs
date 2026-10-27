namespace Massdex.SyncShark.V2;

public sealed record DeletionClassification(string Cursor, IReadOnlySet<string> Exact, IReadOnlySet<string> Ancestors);
