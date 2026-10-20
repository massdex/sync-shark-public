using System.ComponentModel;
using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task RefusalRefreshFailureChecksAsync()
    {
        foreach (var error in new Exception[]
        {
            new Microsoft.Data.Sqlite.SqliteException("database busy", 5),
            new InvalidDataException("notice identity collision"),
            new StateDamagedException("damaged state"),
            new Microsoft.Data.Sqlite.SqliteException("database corrupt", 11)
        })
        {
            var store = new RecordingStore(new InMemoryStateStore());
            var fx = Build(suppliedStore: store);
            fx.A.Seed("completed.txt", "independent copy");
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
            store.RefusalRefreshError = error;
            var damaged = error is StateDamagedException || error is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 11 };
            try
            {
                var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default);
                Require(!damaged && report.Failed == 0 && await fx.B.ObserveAsync("completed.txt", default) is not null,
                    "refusal catalogue failure never fails completed work");
                Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "refusal-notices-deferred") == 1,
                    "repeated finalization failures raise one deduplicated notice");
            }
            catch (Exception caught) when (damaged && ReferenceEquals(caught, error)) { }
        }
    }

    private static async Task RepairClosureScopeChecksAsync()
    {
        var fx = Build();
        fx.Provider.AddInventory(fx.Pair.A, new ScriptedInventory(fx.A) { Unreadable = ["blocked"] });
        foreach (var path in new[] { "blocked/missing.txt", "healthy/missing.txt" })
        {
            var item = new ItemState(path, ItemKind.File, "id:" + path, "v1", null, 1,
                DateTimeOffset.MinValue, ItemFlags.ProviderNameRefused);
            fx.Store.AddNotices([new(fx.Pair.PairId, 31, NoticeKind.Info, path, "name refused",
                ProviderNameRefusedException.NoticeKey, Repair: new NoticeRepair(Side.A, item))]);
        }
        var run = fx.Run();
        await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var remaining = fx.Store.OpenNotices(fx.Pair.PairId).Where(n => n.Repair is not null).ToArray();
        Require(remaining.Length == 1 && remaining[0].Path == "blocked/missing.txt",
            "an unreadable subtree retains its own missing-item notice, not unrelated deleted-item notices");
    }

    internal static async Task BlockerChecksAsync()
    {
        await RefusalRefreshFailureChecksAsync();
        await RepairClosureScopeChecksAsync();
        Require(BlockerNames.DropboxRefuses("folder/.DS_Store") && BlockerNames.DropboxRefuses("Icon" + (char)13),
            "Dropbox reserved metadata names are rejected before rename");
        foreach (var (code, expected) in new[] { (5, "permissions"), (32, "Close the application"),
            (3, "Locate the moved"), (1167, "Reconnect"), (112, "Free space"), (123, "Rename"), (206, "Shorten") })
            Require(BlockerDiagnostic.Describe(new IOException("identity check failed", new Win32Exception(code)))
                .Contains(expected, StringComparison.Ordinal), "wrapped native repair " + code);
        // Quarantine owns these codes; never accidentally classify them as absence or ordinary permission.
        foreach (var code in new[] { 225, 226 })
            Require(!BlockerDiagnostic.Describe(new Win32Exception(code)).Contains("Repair:", StringComparison.Ordinal),
                "quarantine is not reclassified by the blocker catalog");
        foreach (var tag in new[] { "insufficient_space", "no_write_permission", "operation_suppressed", "malformed_path", "team_folder", "restricted_content" })
        {
            var raw = new DropboxApiException(HttpStatusCode.Conflict, "provider refusal") { WriteErrorTag = tag };
            var mapped = DropboxItems.MapWriteError(new System.Reflection.TargetInvocationException(raw));
            Require(mapped is ProviderWriteBlockedException blocked && blocked.Code == tag,
                "structured provider refusal remains distinct from target mismatch: " + tag);
            Require(ReferenceEquals(mapped, DropboxItems.MapWriteError(mapped)), "write mapping is idempotent");
        }
        var staging = new IOException("Upload session start failed", new DropboxApiException(HttpStatusCode.Conflict, "staging conflict") { WriteErrorTag = "conflict" });
        Require(ReferenceEquals(staging, DropboxItems.MapWriteError(staging)), "staging wrapper is not reclassified as a target mismatch");
        Require(DropboxItems.MapWriteError(new IOException("session start rejected",
            new DropboxApiException(HttpStatusCode.Conflict, "quota") { WriteErrorTag = "insufficient_space" }))
            is ProviderWriteBlockedException { Code: "insufficient_space" }, "quota remains actionable beneath a staging wrapper");
        var unknown = new DropboxApiException(HttpStatusCode.Conflict, "unknown") { WriteErrorTag = "future_reason" };
        Require(ReferenceEquals(unknown, DropboxItems.MapWriteError(unknown)), "unknown 409 remains a genuine retryable failure");
        Require(DropboxItems.MapWriteError(new DropboxApiException(HttpStatusCode.Conflict, "changed")
            { WriteErrorTag = "conflict" }) is ExpectedTargetMismatchException, "exact conflict remains conditional mismatch");
        var paper = DropboxItems.ToItem(new PilotRemoteEntry("Document.paper", PilotEntryKind.File, "id:document", "revision", 0, null)
            { RequiresExport = true });
        Require(paper.Flags.HasFlag(ItemFlags.RequiresExport) && paper.Flags.HasFlag(ItemFlags.Unreadable),
            "non-downloadable metadata survives mapping without turning into deletion evidence");

        var options = new PlanOptions(TimeSpan.Zero, false,
            new LabelRule("{name}", "yyyy", TimeSpan.Zero, "A", "B"), 10);
        var colonPaper = paper with { Path = "Plan: Q3.paper", IdentityJson = DropboxNameMapping.Fact("Plan: Q3.paper") };
        Plan Names(params ItemState[] items) => Planner.Decide("p1", 1,
            items.Select(item => new PlanRow(null, null, item, new(false, false, true, true))).ToArray(), options);
        Require(Names(colonPaper).Notices.Single().ReasonKey == "export-required",
            "mapped colon Paper documents offer export guidance, never Rename");
        var mappedUnreadable = paper with { Path = "a?b", Flags = ItemFlags.Unreadable, IdentityJson = DropboxNameMapping.Fact("a?b") };
        Require(Names(mappedUnreadable).Notices.All(n => n.Repair is null), "mapped names are not Windows-invalid");
        var parent = new ItemState("CON", ItemKind.Folder, "id:parent", null, null, 0, DateTimeOffset.MinValue, ItemFlags.Unreadable);
        var children = Enumerable.Range(0, 3).Select(i => paper with { Path = "CON/child" + i, ObjectId = "child" + i, Flags = ItemFlags.Unreadable }).ToArray();
        var invalidFolder = Names(new[] { parent }.Concat(children).ToArray()).Notices;
        Require(invalidFolder.Count == 1 && invalidFolder[0].Repair?.Item.ObjectId == parent.ObjectId,
            "one actionable invalid-folder notice suppresses all descendant notices");

        var root = Path.Combine(Path.GetTempPath(), "blocker-check-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store, displayRoot: true);
        var pair = fx.Pair.PairId;
        store.AddNotices([new(pair, 30, NoticeKind.Info, "changing.txt", "content not confirmed", "content-unconfirmed")]);
        store.AddNotices([new(pair, 40, NoticeKind.Info, "changing.txt", "exact executor failure", "sync-retry")]);
        store.AddNotices([new(pair, 30, NoticeKind.Info, "changing.txt", "content not confirmed", "content-unconfirmed")]);
        var single = store.OpenNotices(pair).Single();
        Require(single.ReasonKey == "sync-retry" && single.Detail == "exact executor failure", "one notice preserves the specific diagnosis");
        var run = fx.Run();
        await run.ApplyRetryNoticeChoiceAsync(pair, single.NoticeId!, NoticeChoices.RetryNow, default);
        Require(store.SyncRetryAt(pair, single.Path)?.RetryAfter == DateTimeOffset.MinValue,
            "targeted retry creates work even when no previous retry row exists");
        Require(store.OpenNotices(pair).Count == 1, "requesting retry does not falsely close the notice");
        var guidance = await run.ApplyRetryNoticeChoiceAsync(pair, single.NoticeId!, NoticeChoices.RepairHelp, default);
        Require(guidance.Contains("exact executor failure", StringComparison.Ordinal) && store.OpenNotices(pair).Count == 1,
            "guided repair is shared and does not acknowledge success");

        var host = new V2SyncHost(store, run, new ObservedExecutor(fx.Executor), null!, Settings, TimeProvider.System,
            Path.Combine(root, "state.db"));
        var guiResult = await host.ResolveConflictSelectionCommandAsync(
            [new(new(single.NoticeId!, pair, single.Path, 0), SyncAuthorityConflictResolutionKind.PreserveBoth,
                NoticeChoices.RepairHelp)]);
        Require(guiResult.Items.Single().Result!.Decisions.Single().Reason.Contains("exact executor failure", StringComparison.Ordinal),
            "GUI host uses shared repair guidance");
        var cli = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
        await cli.ResolveConflictAsync(pair, single.Path, NoticeChoices.RepairHelp, single.NoticeId, 0, null, default);
        Require(store.OpenNotices(pair).Count == 1, "CLI repair guidance leaves the same notice open");

        foreach (var key in new[] { "unreadable", NoticeChoices.ItemBlocked })
        {
            var path = key + ".txt";
            var before = fx.A.Seed(path, "untouched");
            store.AddNotices([new(pair, 40, NoticeKind.Info, path, "cannot read this item", key)]);
            var generic = store.OpenNotices(pair).Single(n => n.Path == path);
            Require(NoticeChoices.Options(generic).Select(option => option.Choice).SequenceEqual(
                new[] { NoticeChoices.RepairHelp, NoticeChoices.RetryNow, NoticeChoices.Ignore }),
                "unreadable and undiscovered item blockers have why, Retry and Ignore");
            await run.ApplyRetryNoticeChoiceAsync(pair, generic.NoticeId!, NoticeChoices.RetryNow, default);
            Require(store.SyncRetryAt(pair, path)?.RetryAfter == DateTimeOffset.MinValue, "generic Retry schedules the item");
            await run.ApplyRetryNoticeChoiceAsync(pair, generic.NoticeId!, NoticeChoices.Ignore, default);
            Require(store.Pair(pair)!.Excluded.Contains(path) && await fx.A.ObserveAsync(path, default) == before,
                "generic Ignore excludes the path without deleting its file");
        }
        Require(!NoticeChoices.IsRetryNotice(new(pair, 40, NoticeKind.Info, "pending.txt", "pending evidence", "deletion-evidence-pending")),
            "acknowledgement notices are not item blockers");
        var refusedRows = Enumerable.Range(0, 205).Select(i => new ItemState("refused-" + i,
            ItemKind.File, "refused-id-" + i, "v1", null, 0, DateTimeOffset.MinValue, ItemFlags.ProviderNameRefused)).ToArray();
        store.WriteSnapshot(new Snapshot(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "test"), "c", refusedRows, []));
        store.RefreshProviderNameNotices(fx.Pair);
        Require(store.OpenNotices(pair).Count(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey) == 205,
            "refusal catalog streams multiple pages into one store transaction");
        var firstRefusal = store.OpenNotices(pair).Single(n => n.Path == refusedRows[0].Path);
        var newerRefusal = refusedRows[0] with { Path = "still-refused", VersionId = "v2" };
        store.AddNotices([firstRefusal with { Path = newerRefusal.Path, Repair = new NoticeRepair(Side.A, newerRefusal) }]);
        var refreshed = store.NoticeById(pair, firstRefusal.NoticeId!)!.Value.Notice;
        Require(refreshed.Path == newerRefusal.Path && refreshed.Repair?.Item.VersionId == "v2" &&
            store.OpenNotices(pair).Count(n => n.Repair?.Item.ObjectId == newerRefusal.ObjectId) == 1,
            "next observation refreshes revision and reuses the exact notice ID after a refused-name move");
        Require(store.LiveFlaggedPage(fx.Pair.A.RootId, ItemFlags.ProviderNameRefused,
            refusedRows[0] with { Path = "missing-parent/child" }, 100).Count == 0,
            "missing paging directory terminates rather than restarting");
        var repeatedPage = refusedRows.Take(100).ToArray();
        Require(ProviderNameNotices.Read(fx.Pair, (_, _) => repeatedPage).Count() == 200,
            "nonadvancing page terminates independently for both sides");
        Console.WriteLine("PASS blocker prerequisites, structured repairs, dedupe, targeted retry, generic ignore");
    }
}
