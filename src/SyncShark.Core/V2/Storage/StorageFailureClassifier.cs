namespace Massdex.SyncShark.V2;

/// <summary>Storage-owned damage recognition; other failure scopes remain shallow.</summary>
internal static class StorageFailureClassifier
{
    internal static bool IsStoreException(Exception error) =>
        error is Microsoft.Data.Sqlite.SqliteException;

    internal static bool IsDirectStateDamage(Exception error) =>
        error is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 11 or 26 };

    internal static bool IsBusy(Exception error) =>
        error is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 };

    internal static int? DirectErrorCode(Exception error) =>
        error is Microsoft.Data.Sqlite.SqliteException sqlite ? sqlite.SqliteErrorCode : null;

    internal static bool IsStateDamage(Exception error)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (current is StateDamagedException or StateNeedsFreshDatabaseException ||
                current is Microsoft.Data.Sqlite.SqliteException sqlite && StoreSchema.IsDamaged(sqlite))
                return true;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }

    internal static FailureRetryOwner.FailureScope ScopeOf(Exception error, CancellationToken ct) =>
        FailureRetryOwner.ScopeOf(error, ct, IsStateDamage);
}
