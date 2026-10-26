namespace Massdex.SyncShark.V2;

/// <summary>Failed delta history reads are not negative evidence; retained independently of cache validation.</summary>
public sealed record DeletionEvidenceFailure(int Attempts, DateTimeOffset FirstUtc, long Generation, bool Released);
