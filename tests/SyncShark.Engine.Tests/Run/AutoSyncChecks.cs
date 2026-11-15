using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks the AutoSync/Repair prerequisite durability boundary (`--engine-checks run`). A running
/// Repair's listing-page retry publishes the existing RequiredRepairRetry notice through the store's
/// current-operation owner without advancing durable pair scheduling; an actually unsuccessful enclosing
/// Repair records that episode once. Page success, cancellation and pair disable close only the page-wait
/// contribution, and an independently protected failed-Repair notice survives. The executable authority is
/// the shared StateStore/IssueTable seam AutoSync and Repair consume; no sleep or page counter is used.</summary>
public static class AutoSyncChecks
{
    // Neutral synthetic identities describe the contract; they are not production configuration.
    private const string PairId = "auto-pair";
    private const string OtherPair = "auto-other";
    private const string LocalRoot = "X:/Parent";
    private const string RemoteRoot = "/Parent";
    private const string Account = "account";
    private const string ContractAssertion = "autosync-prerequisite-contract";
    private const string Database = nameof(AutoSyncChecks) + StateSchema.DatabaseExtension;
    private const double MidpointSample = 0.5;
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static void Run()
    {
        WithStore(FirstPageFailureCases);
        WithStore(RepeatedPageFailureCases);
        WithStore(PageSuccessCases);
        WithStore(CancelCases);
        WithStore(DisableCases);
        WithStore(StaleIdentityCases);
        WithStore(IndependentDebtCases);
        WithStore(ActualRepairOnceOnlyCases);
        WithStore(NoticeWriteFailureCases);
    }

    // The first transient prerequisite-page failure raises the one existing retry notice through the
    // current-operation owner, and nothing durable is scheduled.
    private static void FirstPageFailureCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local);
        var remote = store.ReadSource(PairId, Side.Dropbox);
        var operation = new object();

        var notice = store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice());

        Require(notice.Kind == IssueKind.Failure && notice.Cause == TextKey.RequiredRepairRetry &&
            notice.Path.Length == 0, "a first page failure raises the existing RequiredRepairRetry notice");
        Require(Only(store, notice), "the page-wait notice is the pair's only issue");
        Require(store.RetryNoticeProtected(PairId, notice.Id),
            "the page-wait notice is protected while a prerequisite obligation remains");
        Require(store.ReadPrerequisiteRetry(PairId) is null,
            "a running page retry schedules no pair attempt or deadline");
        Require(Generation(store) == generation, "a running page retry changes no pair generation");
        Require(store.ReadSource(PairId, Side.Local) == local && store.ReadSource(PairId, Side.Dropbox) == remote,
            "a running page retry preserves both source obligations");
    }

    private static void RepeatedPageFailureCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var operation = new object();
        var first = store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice());
        var repeated = store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice());

        Require(first.Id == repeated.Id && first.At == repeated.At && Only(store, first),
            "repeated page failures keep one notice and do not refresh it");
        Require(store.ReadPrerequisiteRetry(PairId) is null && Generation(store) == generation,
            "repeated page failures never advance durable pair scheduling");
    }

    private static void PageSuccessCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local);
        var remote = store.ReadSource(PairId, Side.Dropbox);
        var operation = new object();
        var notice = store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice());

        store.CompletePrerequisitePageWait(PairId, generation, operation, notice);

        Require(store.Issues(PairId).Count == 0, "the first successful page closes the page-wait-only notice");
        Require(store.ReadPrerequisiteRetry(PairId) is null && Generation(store) == generation,
            "page success is not durable pair accounting");
        Require(store.ReadSource(PairId, Side.Local) == local && store.ReadSource(PairId, Side.Dropbox) == remote,
            "page success preserves both source obligations");
    }

    private static void CancelCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local);
        var remote = store.ReadSource(PairId, Side.Dropbox);
        var firstOperation = new object();
        var secondOperation = new object();
        var first = store.RecordPrerequisitePageWait(PairId, generation, firstOperation, PageWaitNotice());
        var second = store.RecordPrerequisitePageWait(PairId, generation, secondOperation, PageWaitNotice());

        Require(first.Id == second.Id && Only(store, first), "two operations share one page-wait notice");
        store.CompletePrerequisitePageWait(PairId, generation, firstOperation, first);
        Require(Only(store, first), "cancelling one operation leaves the other page-wait contribution");
        store.CompletePrerequisitePageWait(PairId, generation, secondOperation, second);
        Require(store.Issues(PairId).Count == 0, "cancelling the last operation closes the page-wait-only notice");
        Require(store.ReadPrerequisiteRetry(PairId) is null && Generation(store) == generation,
            "page cancellation is not durable pair accounting");
        Require(store.ReadSource(PairId, Side.Local) == local && store.ReadSource(PairId, Side.Dropbox) == remote,
            "page cancellation preserves both source obligations");
    }

    private static void DisableCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var operation = new object();
        var notice = store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice());
        store.CompletePrerequisitePageWait(PairId, generation, operation, notice);

        var execution = store.ReadPairExecution(PairId)!.Value;
        store.SavePairDecision(execution.Pair, execution.Generation,
            execution.Pair with { Enabled = false }, null, false, null);

        Require(store.Issues(PairId).Count == 0 && store.ReadPrerequisiteRetry(PairId) is null,
            "disabling a pair retains no page-wait-only notice and schedules no episode");
        Require(Generation(store) == execution.Generation + 1, "disable advances the pair generation once");
    }

    private static void StaleIdentityCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var operation = new object();
        var notice = store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice());

        Refuses<InvalidOperationException>(() =>
            store.CompletePrerequisitePageWait(PairId, generation, new object(), notice), "unknown operation");
        Refuses<InvalidOperationException>(() =>
            store.CompletePrerequisitePageWait(PairId, generation, operation, notice with { Id = notice.Id + 1 }),
            "different notice identity");
        Refuses<InvalidOperationException>(() =>
            store.CompletePrerequisitePageWait(OtherPair, generation, operation, notice), "different pair");
        Refuses<InvalidOperationException>(() =>
            store.CompletePrerequisitePageWait(PairId, generation + 1, operation, notice), "stale close generation");
        Refuses<LiveAdmissionStaleException>(() =>
            store.RecordPrerequisitePageWait(PairId, generation + 1, operation, PageWaitNotice()),
            "stale raise generation");
        Require(Only(store, notice) && store.ReadPrerequisiteRetry(PairId) is null,
            "a stale identity or generation refuses and writes nothing");
    }

    private static void IndependentDebtCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var episode = FailureRetryOwner.AdoptedFailed(0, Now, null, ShippedBackoff(), () => MidpointSample);
        store.RecordPrerequisiteFailure(PairId, generation, local.Epoch, remote.Epoch, local.Revision,
            remote.Revision, null, episode, PageWaitNotice());

        var operation = new object();
        var notice = store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice());
        Require(Only(store, notice), "the page wait reuses the protected failed-Repair notice");
        store.CompletePrerequisitePageWait(PairId, generation, operation, notice);
        Require(store.ReadPrerequisiteRetry(PairId) == episode,
            "page success does not clear the durable pair episode");
        Require(Only(store, notice) && store.RetryNoticeProtected(PairId, notice.Id),
            "the independently protected failed-Repair notice survives page success");

        store.Commit(PairId, StateChange.Empty with { Close = [(string.Empty, IssueClosure.AfterEffect)] });
        Require(Only(store, notice), "generic closure cannot delete the protected retry notice");
        store.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailChanged, string.Empty, new IssueSubject([], null))]
        });
        Require(store.Issues(PairId).Single().Cause == TextKey.RequiredRepairRetry,
            "a generic root failure cannot re-cause the protected retry notice");

        var execution = store.ReadPairExecution(PairId)!.Value;
        store.SavePairDecision(execution.Pair, execution.Generation,
            execution.Pair with { Enabled = false }, null, false, null);
        Require(store.ReadPrerequisiteRetry(PairId) == episode &&
            store.Issues(PairId).Single().Cause == TextKey.RequiredRepairRetry &&
            store.ReadSource(PairId, Side.Local)!.Value.Obligation is not null &&
            store.ReadSource(PairId, Side.Dropbox)!.Value.Obligation is not null,
            "disable preserves the episode and notice and installs successor obligations");
    }

    // A running page retry is not next-Repair durable scheduling; an actually unsuccessful enclosing
    // Repair records its episode once through the shared owner and each later failure advances once.
    private static void ActualRepairOnceOnlyCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var backoff = ShippedBackoff();
        var pageOperation = new object();
        var pageNotice = store.RecordPrerequisitePageWait(PairId, generation, pageOperation, PageWaitNotice());
        Require(store.ReadPrerequisiteRetry(PairId) is null,
            "a running page retry is not next-Repair durable scheduling");

        var first = FailureRetryOwner.AdoptedFailed(0, Now, null, backoff, () => MidpointSample);
        store.RecordPrerequisiteFailure(PairId, generation, local.Epoch, remote.Epoch, local.Revision,
            remote.Revision, null, first, PageWaitNotice());
        Require(store.ReadPrerequisiteRetry(PairId) == first && first.Attempts == 1 && Only(store, pageNotice),
            "the first actually unsuccessful Repair records its episode once on the one notice");

        Refuses<InvalidOperationException>(() =>
            store.RecordPrerequisiteFailure(PairId, generation, local.Epoch, remote.Epoch, local.Revision,
                remote.Revision, null, first, PageWaitNotice()),
            "duplicate failed attempt");
        Require(store.ReadPrerequisiteRetry(PairId) == first && Only(store, pageNotice) &&
            store.ReadSource(PairId, Side.Local)!.Value == local &&
            store.ReadSource(PairId, Side.Dropbox)!.Value == remote,
            "a duplicate failure writes nothing and leaves source obligations unchanged");

        var second = FailureRetryOwner.AdoptedFailed(first.Attempts, Now.AddMinutes(1), null, backoff, () => MidpointSample);
        store.RecordPrerequisiteFailure(PairId, generation, local.Epoch, remote.Epoch, local.Revision,
            remote.Revision, first, second, PageWaitNotice());
        Require(store.ReadPrerequisiteRetry(PairId) == second && second.Attempts == first.Attempts + 1,
            "each actual unsuccessful Repair advances the shared owner's stage once");

        store.CompletePrerequisitePageWait(PairId, generation, pageOperation, pageNotice);
        Require(store.ReadPrerequisiteRetry(PairId) == second &&
            store.Issues(PairId).Single().Cause == TextKey.RequiredRepairRetry,
            "the running page's completion never clears the durable episode or notice");
    }

    private static void NoticeWriteFailureCases(StateStore store, string path)
    {
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local);
        var remote = store.ReadSource(PairId, Side.Dropbox);
        var operation = new object();
        Execute(path, "CREATE TRIGGER injected_notice_failure BEFORE INSERT ON issue " +
            "BEGIN SELECT RAISE(ABORT,'injected-notice-failure'); END;");
        try
        {
            Refuses<SqliteException>(() =>
                store.RecordPrerequisitePageWait(PairId, generation, operation, PageWaitNotice()),
                "page-wait notice write");
            var localValue = local!.Value;
            var remoteValue = remote!.Value;
            Refuses<SqliteException>(() =>
                store.RecordPrerequisiteFailure(PairId, generation, localValue.Epoch, remoteValue.Epoch,
                    localValue.Revision, remoteValue.Revision, null,
                    FailureRetryOwner.AdoptedFailed(0, Now, null, ShippedBackoff(), () => MidpointSample), PageWaitNotice()), "durable notice write");
        }
        finally
        {
            Execute(path, "DROP TRIGGER injected_notice_failure;");
        }

        Require(store.Issues(PairId).Count == 0, "a notice-write failure stores no notice");
        Require(store.ReadPrerequisiteRetry(PairId) is null && Generation(store) == generation,
            "a notice-write failure never reports a scheduled episode or healthy progress");
        Require(store.ReadSource(PairId, Side.Local) == local && store.ReadSource(PairId, Side.Dropbox) == remote,
            "a notice-write failure rolls back the episode and both source obligations");
    }

    private static StateChange PageWaitNotice() => StateChange.Empty with
    {
        Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
    };

    private static long Generation(StateStore store) => store.ReadPairExecution(PairId)!.Value.Generation;

    private static bool Only(StateStore store, Issue notice)
    {
        var issues = store.Issues(PairId);
        return issues.Count == 1 && issues[0].Id == notice.Id && issues[0].Kind == IssueKind.Failure &&
            issues[0].Cause == TextKey.RequiredRepairRetry && issues[0].Path.Length == 0;
    }

    private static FailureRetryOwner.BackoffSettings ShippedBackoff()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return SettingsParser.Parse(document.RootElement.GetRawText(), null).Backoff;
    }

    private static void WithStore(Action<StateStore, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), nameof(AutoSyncChecks) + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, Database);
            using var store = StateStore.Open(path);
            store.SavePair(new PairConfig(PairId, PairId, LocalRoot, RemoteRoot, Account, [], [],
                FirstRunPolicy.NewestWins, true, null));
            action(store, path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Refuses<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(ContractAssertion + ": " + what);
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(ContractAssertion + ": " + what);
    }
}
