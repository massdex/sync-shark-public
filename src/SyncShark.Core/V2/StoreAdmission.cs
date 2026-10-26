namespace Massdex.SyncShark.V2;

internal static class StoreAdmission
{
    private static readonly AsyncLocal<CancellationToken> AdmissionCancellation = new();
    internal static CancellationToken CurrentCancellation => AdmissionCancellation.Value;
    internal static IDisposable WithCancellation(CancellationToken ct)
    {
        var previous = AdmissionCancellation.Value;
        AdmissionCancellation.Value = ct;
        return new CancellationScope(previous);
    }

    private sealed class CancellationScope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => AdmissionCancellation.Value = previous;
    }
}
