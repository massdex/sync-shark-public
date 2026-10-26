namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public long IndexingWorkVersion => IndexingWorkSignal.For(this).Version;
    public Task WaitForIndexingWorkAsync(long afterVersion, CancellationToken ct) =>
        IndexingWorkSignal.For(this).WaitAsync(afterVersion, ct);

    // Used only by indexing eligibility/debt writers, never by reads or gate release.
    private void InIndexingStore(Action body, [System.Runtime.CompilerServices.CallerMemberName] string operationName = "") =>
        InIndexingStore<object?>(() => { body(); return null; }, operationName);

    private T InIndexingStore<T>(Func<T> body, [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        return InStore(() =>
        {
            var before = Convert.ToInt64(StoreSchema.Scalar(_connection, null, "SELECT total_changes();"));
            var result = body();
            if (before != Convert.ToInt64(StoreSchema.Scalar(_connection, null, "SELECT total_changes();")))
                IndexingWorkSignal.Publish(this);
            return result;
        }, operationName);
    }
}
