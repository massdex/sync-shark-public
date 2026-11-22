namespace Massdex.SyncShark.Engine.Executor;

/// <summary>CaseRename operations for the executor module.</summary>
public sealed class CaseRename
{
    /// <summary>Move operation on the supplied facts.</summary>
    public Task<Entry> MoveAsync(ISide side, Entry expected, string destination, string suffix, CancellationToken ct) => throw new NotImplementedException();
}
