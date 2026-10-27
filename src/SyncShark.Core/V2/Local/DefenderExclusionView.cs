namespace Massdex.SyncShark.V2;

public sealed record DefenderExclusionView(bool Authoritative, IReadOnlyList<string> Paths);
