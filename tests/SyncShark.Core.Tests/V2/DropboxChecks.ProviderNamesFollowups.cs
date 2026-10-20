namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // RQ-025/032/035/039/093/094; decisions 64/72/74/80. Real SQLite and the public run pipeline,
    // deterministic endpoint journals, no sleeping and no injected store flags or fabricated plans.
    public static async Task ProviderFollowupsAsync(string root, string scenario = "all")
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("Provider follow-up checks require a fresh empty root.", nameof(root));
        var checks = new (string Name, Func<string, Task> Run)[]
        {
            ("parent-delete-full", r => ProviderRetainedParentAsync(r, true)),
            ("parent-delete-delta", r => ProviderRetainedParentAsync(r, false)),
            ("valid-move", r => ProviderValidFolderRenameAsync(r, true)),
            ("valid-mkdir", r => ProviderValidFolderRenameAsync(r, false)),
            ("move-full", r => ProviderRefusedMoveAsync(r, false, true)),
            ("move-delta", r => ProviderRefusedMoveAsync(r, false, false)),
            ("folder-move-full", r => ProviderRefusedMoveAsync(r, true, true)),
            ("folder-move-delta", r => ProviderRefusedMoveAsync(r, true, false)),
            ("mkdir-full", r => ProviderRefusedMkdirAsync(r, true)),
            ("mkdir-delta", r => ProviderRefusedMkdirAsync(r, false)),
            ("notice", ProviderRefusalNoticeLifecycleAsync),
            ("report", ProviderRefusalReportAsync),
            ("decision", ProviderRefusedDecisionAsync),
            ("recovery-small", r => ProviderRefusedSmallRecoveryAsync(r, false)),
            ("recovery-inherited", r => ProviderRefusedSmallRecoveryAsync(r, true)),
            ("held-descendant", r => ProviderRefusedHeldDescendantAsync(r)),
            ("held-inherited", r => ProviderRefusedHeldDescendantAsync(r, true)),
            ("notice-isolation", r => { StorageChecks.RunRefusalNoticeChecks(r); return Task.CompletedTask; }),
            ("storage-summary", r => { StorageChecks.RunFlaggedSummaryChecks(r); return Task.CompletedTask; }),
            ("existing-provider-names", ProviderNamesAsync)
        };
        if (scenario != "all" && !checks.Any(c => c.Name == scenario))
            throw new ArgumentException("Unknown provider follow-up scenario: " + scenario);
        foreach (var check in checks.Where(c => scenario == "all" || c.Name == scenario))
        {
            await check.Run(Path.Combine(root, check.Name));
            Console.WriteLine("PASS provider follow-up " + check.Name);
        }
    }

    private static async Task ProviderRefusedMoveAsync(string root, bool folder, bool full)
    {
        using var fx = new ProviderFollowupFixture(root);
        var oldPath = folder ? "old-folder" : "old.txt";
        var newPath = folder ? "refused-folder" : "refused.txt";
        fx.A.Seed(oldPath, folder ? "" : "original", folder ? ItemKind.Folder : ItemKind.File);
        if (folder) fx.A.Seed(oldPath + "/existing.txt", "existing child");
        await fx.FirstAsync();
        await fx.NextAsync(false); // establish both endpoint cursors before moving
        var oldTarget = await fx.B.ObserveAsync(oldPath, CancellationToken.None);
        Require(oldTarget is not null, "setup: synchronized old target exists");
        await ProviderRenameAsync(fx.A, oldPath, newPath);
        if (folder) fx.A.Seed(newPath + "/new/deep.txt", "new child after move");
        fx.WriterB.Refused.Add(newPath);
        fx.ClearEffects();
        var refusedReport = await fx.NextAsync(false);
        Require(refusedReport.Moved == 0 && refusedReport.Uploaded == 0 && refusedReport.Done == 0,
            "refused move and blocked descendants never report completed moves or uploads");
        var refusalNotice = fx.Store.OpenNotices("p1").Single(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey);
        Require(refusalNotice.Detail.Contains("old path", StringComparison.OrdinalIgnoreCase),
            "move refusal notice explicitly explains preservation of the existing copy at its old path");
        Require(fx.WriterB.Effects.Count(e => e.Kind == "move" && e.Path == newPath) == 1,
            "setup: actual planned move reached the refusing endpoint exactly once");
        Require(await fx.B.ObserveAsync(oldPath, CancellationToken.None) == oldTarget,
            "refused move preserves old target identity and content immediately");
        ProviderAssertOnlyFlag(fx, newPath);
        if (folder)
        {
            Require(!fx.WriterB.Effects.Any(e => PairPath.IsUnder(e.Path, newPath)),
                "refused folder move suppresses all child writes and implicit parent creation in the same run");
            Require(await fx.B.ObserveAsync(oldPath + "/existing.txt", CancellationToken.None) is not null,
                "refused folder move keeps the old target subtree");
        }
        // Reopen SQLite and construct a new SyncRun: no in-memory suppression can satisfy the next checks.
        fx.Reopen();
        fx.ClearEffects();
        await fx.NextAsync(full);
        Require(await fx.B.ObserveAsync(oldPath, CancellationToken.None) == oldTarget &&
            !fx.WriterA.Effects.Any() && !fx.WriterB.Effects.Any(),
            "later scan preserves old move target and performs no refusal/descendant retry");
        // Only B changes. A's refused source is absent from the delta touched set. Matching the refusal
        // by current path alone must not let the linked old target be deleted, restored or overwritten.
        var changedPath = folder ? oldPath + "/existing.txt" : oldPath;
        var changed = fx.B.Seed(changedPath, "independent target edit", mtime: DateTimeOffset.UnixEpoch.AddDays(10));
        if (!full)
        {
            var cursor = fx.Store.Cursor("ra");
            Require(cursor is not null, "setup: source has a stored delta cursor");
            var untouched = await fx.A.ChangesSinceAsync(cursor!, 0, _ => true, CancellationToken.None);
            Require(untouched.Changes.Count == 0, "setup: refused source is absent from the next delta touched set");
        }
        fx.ClearEffects();
        await fx.NextAsync(full);
        Require(await fx.B.ObserveAsync(changedPath, CancellationToken.None) == changed,
            "target-only change remains intact while linked source name is refused");
        Require(fx.WriterA.Effects.Count == 0 && fx.WriterB.Effects.Count == 0,
            "target-only touched set cannot bypass persisted move refusal protection");
        ProviderAssertOnlyFlag(fx, newPath);
    }

    private static async Task ProviderRefusedMkdirAsync(string root, bool full)
    {
        using var fx = new ProviderFollowupFixture(root);
        fx.A.Seed("refused", "", ItemKind.Folder);
        fx.A.Seed("refused/nested", "", ItemKind.Folder);
        fx.A.Seed("refused/nested/child.txt", "unique child");
        fx.A.Seed("healthy.txt", "unrelated work");
        fx.WriterB.Refused.Add("refused");
        var report = await fx.FirstAsync();
        Require(fx.WriterB.Effects.Count(e => e.Kind == "mkdir" && e.Path == "refused") == 1,
            "refused parent mkdir is attempted only once, including implicit parent attempts");
        Require(!fx.WriterB.Effects.Any(e => PairPath.IsUnder(e.Path, "refused")),
            "same run never submits refused folder descendants to the provider");
        ProviderAssertOnlyFlag(fx, "refused");
        Require(fx.Store.FirstSyncDone("p1") && report.Failed == 0,
            "refused folder and blocked descendants settle first sync without failures");
        Require(await fx.B.ObserveAsync("healthy.txt", CancellationToken.None) is not null,
            "unrelated executable work completes despite refused subtree");
        fx.Reopen();
        fx.ClearEffects();
        await fx.NextAsync(full); // establish cursors after first-sync preview
        Require(fx.WriterA.Effects.Count == 0 && fx.WriterB.Effects.Count == 0,
            "first reopened scan does not retry a refused folder or any unchanged descendant");
        fx.A.Seed("refused/nested/child.txt", "changed child");
        fx.A.Seed("refused/another.txt", "new child");
        fx.ClearEffects();
        await fx.NextAsync(full);
        Require(fx.WriterA.Effects.Count == 0 && fx.WriterB.Effects.Count == 0,
            "later full/delta scan suppresses changed and new descendants without provider effects");
        ProviderAssertOnlyFlag(fx, "refused");
        Require(await fx.B.ObserveAsync("refused", CancellationToken.None) is null &&
            await fx.B.ObserveAsync("refused/nested/child.txt", CancellationToken.None) is null,
            "implicit parent creation cannot materialize a refused subtree");
    }

    private static async Task ProviderRefusalNoticeLifecycleAsync(string root)
    {
        using var fx = new ProviderFollowupFixture(root);
        foreach (var path in new[] { "rename.txt", "delete.txt", "stay.txt" })
        {
            fx.A.Seed(path, path);
            fx.WriterB.Refused.Add(path);
        }
        await fx.FirstAsync();
        fx.Reopen();
        await fx.NextAsync(false);
        ProviderAssertNotice(fx, 3, ["rename.txt", "delete.txt", "stay.txt"], []);
        // Changing the refused set without adding another refusal must update a store-derived summary.
        await ProviderRenameAsync(fx.A, "rename.txt", "accepted.txt");
        await ProviderDeleteAsync(fx.A, "delete.txt");
        await fx.NextAsync(false);
        ProviderAssertNotice(fx, 1, ["stay.txt"], ["rename.txt", "delete.txt"]);
        fx.A.Seed("new-refusal.txt", "new");
        fx.WriterB.Refused.Add("new-refusal.txt");
        await fx.NextAsync(false);
        ProviderAssertNotice(fx, 2, ["stay.txt", "new-refusal.txt"], ["rename.txt", "delete.txt"]);
        fx.Reopen();
        fx.ClearEffects();
        var stale = fx.Store.OpenNotices("p1").First(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey && n.Path == "stay.txt");
        fx.Store.AddNotices([stale with { Detail = "stale summary" }]);
        await fx.NextAsync(false);
        Require(fx.WriterA.Effects.Count == 0 && fx.WriterB.Effects.Count == 0,
            "notice refresh has a genuinely no-action rerun");
        ProviderAssertNotice(fx, 2, ["stay.txt", "new-refusal.txt"], ["rename.txt", "delete.txt"]);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "v2", "state.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_idle_notice BEFORE UPDATE OF detail ON notice " +
                "WHEN OLD.detail=NEW.detail BEGIN SELECT RAISE(FAIL, 'unchanged notice was written'); END;";
            command.ExecuteNonQuery();
        }
        var idle = await fx.NextAsync(false);
        Require(idle.Failed == 0, "idle run never rewrites an unchanged refusal summary");
        await ProviderDeleteAsync(fx.A, "stay.txt");
        await ProviderRenameAsync(fx.A, "new-refusal.txt", "also-accepted.txt");
        await fx.NextAsync(true);
        Require(!fx.Store.OpenNotices("p1").Any(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey),
            "last refused item gone closes the notice automatically");
    }

    private static async Task ProviderRefusalReportAsync(string root)
    {
        using var fx = new ProviderFollowupFixture(root);
        fx.A.Seed("refused.txt", "unique bytes");
        fx.WriterB.Refused.Add("refused.txt");
        var report = await fx.FirstAsync();
        Require(report.Done == 0 && report.Skipped == 1 && report.Failed == 0 && report.Uploaded == 0,
            "provider refusal reports Skipped, never Done, Failed or Uploaded");
        Require(fx.Store.FirstSyncDone("p1") && fx.Store.UnresolvedIntents("p1").Count == 0,
            "settled refusal does not leave unfinished effects or block first sync");
        Require(fx.Store.Baselines("p1").Count == 0, "refusal does not invent a successful baseline");
    }

    private static async Task ProviderRefusedDecisionAsync(string root)
    {
        using var fx = new ProviderFollowupFixture(root);
        var time = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var source = fx.A.Seed("tie.txt", "local v1", mtime: time);
        var target = fx.B.Seed("tie.txt", "cloud v1", mtime: time.AddSeconds(1));
        await fx.FirstAsync();
        var notice = fx.Store.OpenNotices("p1").Single(n => n.Kind == NoticeKind.UserDecision);
        var snapshot = (await fx.Run.ObserveDecisionsAsync("p1", [(notice.NoticeId!, notice.Path)],
            CancellationToken.None)).Single();
        fx.WriterB.Refused.Add("tie.txt");
        var report = await fx.Run.ResolveDecisionAsync("p1", notice.Path, snapshot.Token, Side.A,
            new RecordingProgress(), CancellationToken.None);
        Require(fx.WriterB.Effects.Any(e => e.Kind == "put" && e.Path == "tie.txt"),
            "setup: user decision attempted the selected overwrite");
        Require(fx.Store.OpenNotices("p1").Any(n => n.NoticeId == notice.NoticeId &&
                n.Detail.Contains("disallowed_name", StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(report.DecisionFailureReason),
            "refused decision keeps its UserDecision notice open and explains why it was not applied");
        Require(report.Done == 0 && report.Uploaded == 0 && report.Skipped == 1 && report.Failed == 0,
            "refused decision reports Skipped, neither a successful effect nor a synthetic failure");
        Require(report.DecisionFailureReason?.Contains("disallowed_name", StringComparison.Ordinal) == true,
            "unapplied decision explanation identifies the exact provider refusal");
        fx.Reopen();
        fx.ClearEffects();
        await fx.NextAsync(true);
        Require(fx.Store.OpenNotices("p1").Any(n => n.NoticeId == notice.NoticeId),
            "ordinary later scan cannot silently close the refused decision");
        Require(await fx.A.ObserveAsync("tie.txt", CancellationToken.None) == source &&
            await fx.B.ObserveAsync("tie.txt", CancellationToken.None) == target &&
            fx.WriterA.Effects.Count == 0 && fx.WriterB.Effects.Count == 0,
            "pending refused decision preserves both versions without retry effects");
        var fresh = (await fx.Run.ObserveDecisionsAsync("p1", [(notice.NoticeId!, notice.Path)], CancellationToken.None)).Single();
        var download = await fx.Run.ResolveDecisionAsync("p1", notice.Path, fresh.Token, Side.B,
            new RecordingProgress(), CancellationToken.None);
        Require(download.Downloaded == 1 && download.Failed == 0 && download.DecisionFailureReason is null &&
            fx.Store.OpenNotices("p1").All(n => n.NoticeId != notice.NoticeId),
            "after refused keep-local the user can keep Dropbox without renaming");
        Require((await fx.A.ObserveAsync("tie.txt", CancellationToken.None))?.Hash == target.Hash,
            "opposite choice downloads the selected Dropbox bytes");
        Require(fx.Store.LiveFlaggedSummary("ra", ItemFlags.ProviderNameRefused, 3).Count == 0 &&
            fx.Store.OpenNotices("p1").All(n => n.ReasonKey != ProviderNameRefusedException.NoticeKey),
            "successful download clears the persisted refusal and closes its summary");
        fx.Reopen();
        fx.WriterB.Refused.Clear();
        fx.ClearEffects();
        var edited = fx.A.Seed("tie.txt", "local edit after accepted download", mtime: time.AddDays(1));
        var next = await fx.NextAsync(false);
        Require(next.Uploaded == 1 && next.Failed == 0 &&
            (await fx.B.ObserveAsync("tie.txt", CancellationToken.None))?.Hash == edited.Hash,
            "download remedy remains unflagged after reopen and the next local edit syncs normally");
    }

    private static void ProviderAssertOnlyFlag(ProviderFollowupFixture fx, string path)
    {
        var flagged = fx.Store.Live("ra").Where(i => i.Flags.HasFlag(ItemFlags.ProviderNameRefused)).ToArray();
        Require(flagged.Length == 1 && flagged[0].Path == path,
            "only actually refused source item is flagged; descendants never acquire synthetic refusal flags");
        Require(!fx.Store.Live("rb").Any(i => i.Flags.HasFlag(ItemFlags.ProviderNameRefused)),
            "refusal flag belongs to the source item, not its target or pair");
    }

    private static void ProviderAssertNotice(ProviderFollowupFixture fx, int count, string[] present, string[] absent)
    {
        var notices = fx.Store.OpenNotices("p1").Where(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey).ToArray();
        Require(present.Length == count && present.All(path => notices.Count(n => n.Path == path && n.Repair is not null) == 1) &&
            notices.Length == count && absent.All(path => notices.All(n => n.Path != path)),
            "current refused entities have one actionable notice each; deleted or repaired entities close automatically");
    }

    private static async Task ProviderRenameAsync(FakeEndpoint endpoint, string from, string to)
    {
        var item = await endpoint.ObserveAsync(from, CancellationToken.None);
        await endpoint.MoveAsync(new SyncAction("user-rename", 9, ActionKind.Move, Side.A, to, from,
            null, item, false, null), CancellationToken.None);
    }

    private static async Task ProviderDeleteAsync(FakeEndpoint endpoint, string path)
    {
        var item = await endpoint.ObserveAsync(path, CancellationToken.None);
        await endpoint.DeleteAsync(new SyncAction("user-delete", 6, ActionKind.Delete, Side.A, path, null,
            null, item, false, null), CancellationToken.None);
    }

    private sealed class ProviderFollowupFixture : IDisposable
    {
        private readonly string _root;
        private readonly V2Settings _settings = StorageChecks.Fast with { MaxWorkers = 4, ProgressInterval = TimeSpan.Zero };
        private readonly PairDef _pair = new("p1", "provider follow-ups",
            new RootDef("ra", SyncProviders.Local, null, "C:\\synthetic", "id:local"),
            new RootDef("rb", SyncProviders.Dropbox, "account", "/synthetic", "id:cloud"), [], [], true);
        public FakeEndpoint A { get; } = new(Side.A, "ra");
        public FakeEndpoint B { get; } = new(Side.B, "rb");
        public ProviderFollowupWriter WriterA { get; }
        public ProviderFollowupWriter WriterB { get; }
        public SqliteStateStore Store { get; private set; }
        public SyncRun Run { get; private set; } = null!;
        public ProviderFollowupFixture(string root)
        {
            _root = root;
            WriterA = new(A);
            WriterB = new(B);
            Store = SqliteStateStore.Open(root, _settings, TimeProvider.System);
            Store.SavePair(_pair);
            CreateRun();
        }
        private void CreateRun() => Run = new SyncRun(Store,
            new Executor(Store, _settings, ExecutorChecks.Admission()),
            new FakeEndpointProvider().Add(_pair.A, A).Add(_pair.B, B)
                .AddWriter(_pair.A, WriterA).AddWriter(_pair.B, WriterB), _settings, TimeProvider.System);
        public void Reopen()
        {
            Store.Dispose();
            Store = SqliteStateStore.Open(_root, _settings, TimeProvider.System);
            CreateRun();
        }
        public async Task<RunReport> FirstAsync()
        {
            var plan = await Run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
            return await Run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None);
        }
        public async Task<RunReport> NextAsync(bool full)
        {
            if (full) await Run.RequestRescanAsync(["ra", "rb"], CancellationToken.None);
            var fullBefore = A.FullCalls;
            var deltaBefore = A.DeltaCalls;
            var hadCursor = Store.Cursor("ra") is not null;
            var result = await Run.IncrementalAsync("p1", new RecordingProgress(), CancellationToken.None);
            Require(full ? A.FullCalls > fullBefore : !hadCursor || A.DeltaCalls > deltaBefore,
                "requested inventory mode actually ran");
            return result;
        }
        public void ClearEffects() { WriterA.Effects.Clear(); WriterB.Effects.Clear(); }
        public void Dispose() => Store.Dispose();
    }

    // Refusal is injected at the provider boundary, not in Executor/SyncRun/store. Like Dropbox Put,
    // this writer attempts implicit parent creation; logging before refusal catches those attempts too.
    private sealed class ProviderFollowupWriter(FakeEndpoint inner) : IEndpointWriter
    {
        public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);
        public System.Collections.Concurrent.ConcurrentQueue<(string Kind, string Path)> Effects { get; } = new();
        private void Effect(string kind, string path)
        {
            Effects.Enqueue((kind, path));
            if (Refused.Contains(path)) throw new ProviderNameRefusedException(new IOException("disallowed_name"));
        }
        public async Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        {
            Effect("put", action.Path);
            var parts = action.Path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var parent = string.Join("/", parts.Take(i));
                if (await inner.ObserveAsync(parent, ct) is null) await MkDirAsync(parent, ct);
            }
            return await inner.PutAsync(action, content, ct);
        }
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct)
        { Effect("mkdir", path); return inner.MkDirAsync(path, ct); }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct)
        { Effect("move", action.Path); return inner.MoveAsync(action, ct); }
        public Task DeleteAsync(SyncAction action, CancellationToken ct)
        { Effect("delete", action.Path); return inner.DeleteAsync(action, ct); }
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
        { Effects.Enqueue(("preserve", victim.Path)); return inner.PreserveAsync(victim, actionId, ct); }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct) => inner.OpenReadAsync(item, ct);
    }
}
