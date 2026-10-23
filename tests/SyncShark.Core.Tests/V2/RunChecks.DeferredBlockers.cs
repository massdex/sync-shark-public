using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task DeferredBlockerChecksAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deferred-blockers-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var items = Enumerable.Range(0, 57).Select(i => fx.A.Seed($"stub-{i:D3}.txt", "cloud content") with { Flags = ItemFlags.Placeholder }).ToArray();
        MappingSnapshot(store, fx.Pair.A, Side.A, items);
        string? hydratedPath = null;
        fx.Provider.AddWriter(fx.Pair.A, new SignalingWriter(fx.WriterA)
        { AfterObserve = item => item is null || item.Path == hydratedPath ? item : item with { Flags = ItemFlags.Placeholder } });
        var hydrations = 0;
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System)
        { HydratePlaceholderForTest = (_, item, _) => { hydrations++; hydratedPath = item.Path; return Task.CompletedTask; } };
        store.AddNotices([new(fx.Pair.PairId, LinkExclusions.StubRule, NoticeKind.Info, "", "online-only files", "stubs-left-alone")]);
        var summary = store.OpenNotices(fx.Pair.PairId).Single();
        var pageText = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default);
        var first = store.OpenNotices(fx.Pair.PairId).Where(n => n.ReasonKey == "online-only-item").ToArray();
        Require(first.Length == 50 && hydrations == 0, "online-only listing publishes a bounded page without hydration");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default);
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "online-only-item") == 50,
            "repeated online-only page remains deduplicated");
        foreach (var left in first.Skip(40))
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, left.NoticeId!, NoticeChoices.Ignore, default);
        var token = pageText.Split("Next page token: ")[1].Trim();
        var nextPage = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default, input: new(PageToken: token));
        Require(Enumerable.Range(50, 7).All(i => nextPage.Contains($"stub-{i:D3}.txt", StringComparison.Ordinal)),
            "keyset page reaches all seven remaining stubs after ten earlier items are ignored");
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "online-only-item") == 47,
            "next page reaches the remaining online-only files");
        var chosen = first[0];
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, chosen.NoticeId!, NoticeChoices.MakeAvailableOffline, default);
        Require(hydrations == 1 && store.SyncRetryAt(fx.Pair.PairId, chosen.Path) is not null &&
            store.NoticeById(fx.Pair.PairId, chosen.NoticeId!) is { Closed: false },
            "explicit hydration targets one file, queues re-observation and keeps the notice until fresh evidence");
        var ignored = first[1];
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, ignored.NoticeId!, NoticeChoices.Ignore, default);
        Console.WriteLine($"NOTICE-MEASURE online-ignore excluded={store.Pair(fx.Pair.PairId)!.Excluded.Contains(ignored.Path)} closed={store.NoticeById(fx.Pair.PairId, ignored.NoticeId!)?.Closed} visible={store.OpenNotices(fx.Pair.PairId).Any(n => n.NoticeId == ignored.NoticeId)} writesA={fx.WriterA.Calls} writesB={fx.WriterB.Calls}");
        // E9 / cloud-files.md: Ignore suppresses this cause, not the path. Measured integrated
        // state: excluded=false, closed=false, visible=false, writesA=0, writesB=0.
        Require(!store.Pair(fx.Pair.PairId)!.Excluded.Contains(ignored.Path) && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
            store.NoticeById(fx.Pair.PairId, ignored.NoticeId!) is { Closed: false } &&
            store.OpenNotices(fx.Pair.PairId).All(n => n.NoticeId != ignored.NoticeId),
            "Ignore online-only suppresses the cause while preserving authority, without exclusion or writes");
        store.AddNotices([ignored]);
        Require(store.NoticeById(fx.Pair.PairId, ignored.NoticeId!) is { Closed: false } &&
            store.OpenNotices(fx.Pair.PairId).All(n => n.NoticeId != ignored.NoticeId),
            "same online-only cause stays suppressed and actionable when re-observed");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, ignored.NoticeId!, NoticeChoices.Exclude, default);
        Require(store.Pair(fx.Pair.PairId)!.Excluded.Contains(ignored.Path) && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "explicit Stop syncing this file excludes without writes or deletion");
        foreach (var invalid in new[] { items[0] with { Size = -1 },
            items[0] with { Kind = ItemKind.Folder }, items[0] with { Flags = ItemFlags.Link | ItemFlags.Placeholder },
            items[0] with { Path = "../escape" } })
        {
            try { OnlineOnlyHydration.Validate(invalid); throw new InvalidOperationException("Unsafe hydration accepted"); }
            catch (SyncRunRefusedException) { }
        }
        var failed = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System)
        { HydratePlaceholderForTest = (_, _, _) => throw new IOException("provider unavailable") };
        var failure = await failed.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, first[2].NoticeId!, NoticeChoices.MakeAvailableOffline, default);
        Require(failure.Contains("not verified", StringComparison.Ordinal) &&
            store.NoticeById(fx.Pair.PairId, first[2].NoticeId!)?.Notice.Detail.Contains("provider unavailable", StringComparison.Ordinal) == true,
            "hydration failure remains one actionable notice");
        await LinkActionChecksAsync();
        await HydrationReviewChecksAsync();
        await DeletionIgnoreOutageChecksAsync();
        await DeletionIgnoreReincludeChecksAsync();
        await RestoredHoldReviewChecksAsync();
        await ExportBlockerChecksAsync();
        LegacyRepairNoticeChecks();
        await BlockerGuidanceChecksAsync();
        await RebuildableRuleBlockerChecksAsync();
    }

    private static async Task BlockerGuidanceChecksAsync()
    {
        var fx = Build();
        var run = fx.Run();
        fx.Store.AddNotices([new(fx.Pair.PairId, 40, NoticeKind.Info, "pending.txt", "history pending", "deletion-evidence-pending")]);
        var history = fx.Store.OpenNotices(fx.Pair.PairId).Single();
        var waiting = new SyncRetry(history.Path, "deletion-evidence", 2, "history unavailable", DateTimeOffset.UtcNow.AddHours(1));
        fx.Store.SaveSyncRetry(fx.Pair.PairId, waiting);
        var review = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, history.NoticeId!, NoticeChoices.ReviewDeletion, default);
        Require(review.Contains("history unavailable", StringComparison.Ordinal) && fx.Store.SyncRetryAt(fx.Pair.PairId, history.Path) == waiting,
            "deletion review shows evidence without changing authority");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, history.NoticeId!, NoticeChoices.CheckHistoryAgain, default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, history.Path) == waiting with { RetryAfter = DateTimeOffset.MinValue },
            "history check only resets schedule, preserving all evidence and review guards");
        Require(!NoticeOptions.Options(history).Any(o => o.Choice == NoticeChoices.DeleteReviewed), "unknown evidence cannot grant Delete");
        fx.Store.AddNotices([new(fx.Pair.PairId, 40, NoticeKind.Info, "large.txt", "storage full", "insufficient-free-space",
            FreeSpace: new("X:\\", 100, 50, 25))]);
        var disk = fx.Store.OpenNotices(fx.Pair.PairId).Single(n => n.FreeSpace is not null);
        foreach (var choice in new[] { NoticeChoices.OpenStorage, NoticeChoices.ReviewReserve, NoticeChoices.OpenRecoverySettings, NoticeChoices.OpenFolderSettings })
        {
            var message = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, disk.NoticeId!, choice, default);
            Require(message.Length > 0 && fx.Store.NoticeById(fx.Pair.PairId, disk.NoticeId!) is { Closed: false }, "storage guidance keeps work pending");
        }
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 && fx.Store.Pair(fx.Pair.PairId)!.Excluded.Count == 0,
            "history and storage guidance cause no deletion, copying or exclusion");
    }

    private static void LegacyRepairNoticeChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "legacy-notices-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("legacy.txt", "content");
        var original = new Notice(fx.Pair.PairId, 31, NoticeKind.Info, item.Path, "blocked", "provider-name-refused",
            Repair: new NoticeRepair(Side.A, item, "chosen.txt"));
        store.AddNotices([original]);
        var canonical = store.OpenNotices(fx.Pair.PairId).Single().NoticeId!;
        var legacy = ShortId.Make("n", fx.Pair.PairId, "31", RowCodec.Key(item.Path), "provider-name-refused", "A", item.ObjectId);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(folder)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE notice SET notice_id=$old WHERE notice_id=$new;";
            command.Parameters.AddWithValue("$old", legacy);
            command.Parameters.AddWithValue("$new", canonical);
            command.ExecuteNonQuery();
        }
        store.AddNotices([original with { Repair = new NoticeRepair(Side.A, item) }]);
        Require(store.OpenNotices(fx.Pair.PairId).Single().NoticeId == canonical,
            "legacy path-keyed notice is replaced by one canonical notice");
        Require(store.NoticeById(fx.Pair.PairId, legacy)?.Notice.Repair?.Destination == "chosen.txt",
            "legacy lookup maps to canonical identity and retains its pending destination");
        store.AddNotices([original]);
        Require(store.OpenNotices(fx.Pair.PairId).Count == 1, "legacy migration replay never duplicates notices");
    }

    // C6: run the export-notice checks on the in-memory double and on the real SQLite table.
    private static async Task ExportBlockerChecksAsync()
    {
        await ExportBlockerChecksAsync(null);
        var folder = Path.Combine(Path.GetTempPath(), "export-blockers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        await ExportBlockerChecksAsync(store);
    }

    private static async Task ExportBlockerChecksAsync(IStateStore? supplied)
    {
        var fx = Build(suppliedStore: supplied);
        var paper = fx.B.Seed("Document.paper", "cloud-native") with { Flags = ItemFlags.RequiresExport | ItemFlags.Unreadable };
        fx.Provider.AddWriter(fx.Pair.B, new SignalingWriter(fx.WriterB)
        { AfterObserve = item => item?.Path == paper.Path ? item with { Flags = paper.Flags } : item });
        fx.Store.AddNotices([new(fx.Pair.PairId, 30, NoticeKind.Info, paper.Path, "export required", "export-required")]);
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single();
        var formats = new List<string>();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, TimeProvider.System)
        {
            ExportDocumentForTest = (_, item, format, _) =>
            {
                Require(item.ObjectId == paper.ObjectId, "export uses exact cloud identity");
                formats.Add(format);
                return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("exported " + format)));
            }
        };
        foreach (var choice in new[] { NoticeChoices.ExportDocx, NoticeChoices.ExportMarkdown, NoticeChoices.ExportPdf })
        {
            var result = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, choice, default);
            Require(result.StartsWith("Exported as ", StringComparison.Ordinal), "export action creates a new local file");
        }
        Require(formats.SequenceEqual(new[] { "docx", "markdown", "pdf" }), "all export choices reach the shared exporter");
        Require(fx.Store.ReadSyncRetries(fx.Pair.PairId).Select(r => r.Path).Distinct().Count() == 3 && fx.WriterB.Calls == 0,
            "exports have unique destinations, queue ordinary sync, and never write to the cloud source");
        Require((await fx.B.ObserveAsync(paper.Path, default))?.ObjectId == paper.ObjectId, "original Paper document remains unchanged");
        var failed = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, TimeProvider.System)
        { ExportDocumentForTest = (_, _, _, _) => throw new IOException("format unavailable") };
        await failed.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ExportPdf, default);
        Require(fx.Store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: false } row && row.Notice.Detail.Contains("format unavailable", StringComparison.Ordinal),
            "unsupported format leaves options and one notice instead of hiding the failure");
        using var bytes = new MemoryStream();
        using var bounded = new ExportBoundedStream(bytes);
        try { bounded.SetLength(ExportBoundedStream.Limit + 1); throw new InvalidOperationException("Unbounded export accepted"); }
        catch (IOException) { }
        Require(bytes.Length == 0, "oversize export is refused before growing the spool");
    }
}
