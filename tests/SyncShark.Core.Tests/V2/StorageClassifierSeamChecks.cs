namespace Massdex.SyncShark.V2;

internal static class StorageClassifierSeamChecks
{
    internal static void Run()
    {
        foreach (var code in new[] { 11, 26, 5, 6, 13, 19 })
        {
            var direct = new Microsoft.Data.Sqlite.SqliteException("synthetic", code);
            LocalChecks.Require(StorageFailureClassifier.IsStoreException(direct), "all direct SQLite errors remain store exceptions");
            LocalChecks.Require(StorageFailureClassifier.IsDirectStateDamage(direct) == (code is 11 or 26), "direct damage codes stay 11/26");
            LocalChecks.Require(StorageFailureClassifier.IsBusy(direct) == (code is 5 or 6), "busy codes stay 5/6");
            LocalChecks.Require(StorageFailureClassifier.DirectErrorCode(direct) == code, "diagnostic projection retains exact code");
            foreach (var wrapper in new Exception[] { new IOException("wrapped", direct), new AggregateException(direct) })
            {
                LocalChecks.Require(!StorageFailureClassifier.IsStoreException(wrapper) &&
                    !StorageFailureClassifier.IsDirectStateDamage(wrapper) && !StorageFailureClassifier.IsBusy(wrapper) &&
                    StorageFailureClassifier.DirectErrorCode(wrapper) is null, "all direct predicates remain shallow");
                LocalChecks.Require(StorageFailureClassifier.IsStateDamage(wrapper) == (code is 11 or 26), "deep damage classifier remains unchanged");
            }
            LocalChecks.Require(!ExecutionFallback(direct, _ => false), "injected false damage predicate does not admit direct SQLite fallback");
            LocalChecks.Require(ExecutionFallback(new IOException("wrapped", direct), _ => false), "wrapped SQLite is not a direct exclusion");
        }
        foreach (var error in new Exception[] { new OperationCanceledException(), new StateDamagedException("damage"), new PairPausedException("pair") })
            LocalChecks.Require(!ExecutionFallback(error, _ => false), "execution keeps existing non-SQLite exclusions");
        var rejected = false;
        try { _ = new Executor(null!, null!, null!, _ => false, null!); }
        catch (ArgumentNullException error) { rejected = error.ParamName == "isStoreException"; }
        LocalChecks.Require(rejected, "executor requires explicit direct store classifier");
    }

    // Source-equivalence truth table for the two independently injected parts of the fallback filter.
    private static bool ExecutionFallback(Exception error, Func<Exception, bool> isStateDamage) =>
        !isStateDamage(error) && error is not (PairPausedException or OperationCanceledException or EndpointAccessException or StateDamagedException) &&
        !StorageFailureClassifier.IsStoreException(error);
}
