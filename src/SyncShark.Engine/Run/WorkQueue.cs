namespace Massdex.SyncShark.Engine.Run;

/// <summary>WorkQueue operations for the run module.</summary>
public sealed class WorkQueue
{
    /// <summary>Admit operation on the supplied facts.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change,CancellationToken,Task> apply, CancellationToken ct) => throw new NotImplementedException();
}
