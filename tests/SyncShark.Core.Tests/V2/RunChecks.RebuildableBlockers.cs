using System.Globalization;
using System.Net;
using System.Text;
using Massdex.SyncShark.DropboxCli;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task RebuildableRuleBlockerChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rebuildable-blockers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RebuildableRuleActionsAsync(root);
            RebuildableKeptRuleSuppression(root);
            LegacyMovedRepairChecks(root);
            LegacyIdentityProbeWindowChecks(root);
            LegacyFoldRunsOnceChecks(root);
            await ExportFormatAndNameChecksAsync();
            await ExportMetadataIdentityChecksAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // One notice per unmatched scoped rule; preview is bounded SQL; choose/remove rewrite only the retained review.
    private static async Task RebuildableRuleActionsAsync(string root)
    {
        var fx = Build();
        fx.A.Seed("a", "", ItemKind.Folder);
        fx.A.Seed("a/package.json", "{}");
        fx.A.Seed("a/node_modules", "", ItemKind.Folder);
        fx.A.Seed("a/node_modules/x.js", "local");
        // A name candidate that does not carry the marker: detection ignores it, the bounded SQL preview lists it.
        fx.A.Seed("b", "", ItemKind.Folder);
        fx.A.Seed("b/node_modules", "", ItemKind.Folder);
        const string unmatched = """{"rebuildables":[{"id":"js-packages","prefix":"b"}]}""";
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        var prepared = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, unmatched);
        Require(prepared.Warnings.Any(warning => warning.Contains("matched no folders", StringComparison.Ordinal)),
            "prepare still warns about an unmatched scoped rebuildable");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey);
        Require(notice.RebuildableRule == new NoticeRebuildableRule("js-packages", "b") && notice.Path == "b",
            "one durable notice carries the rule's category and prefix as its identity");
        Require(NoticeChoices.Options(notice).Select(option => option.Choice).SequenceEqual(
            [NoticeChoices.RetryNow, NoticeChoices.PreviewMatches, NoticeChoices.ChooseFolder, NoticeChoices.RemoveRule, NoticeChoices.KeepRule]),
            "the notice offers check again, preview, choose folder, remove and keep");
        var run = fx.Run();
        string Update(PairDef pair, RebuildableChoice current, RebuildableChoice? replacement) =>
            V2CliRuntime.UpdateRebuildableRule(root, pair, fx.Store, current, replacement);
        var preview = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.PreviewMatches,
            default, input: new NoticeActionInput(), updateRebuildableRule: Update);
        Require(preview.Contains("b/node_modules", StringComparison.Ordinal) && preview.Contains("No files were", StringComparison.Ordinal) &&
            fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "preview lists bounded candidate matches without any write or deletion");
        // A name candidate without the category's own marker is still not a detected folder.
        var stillUnmatched = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow,
            default, input: new NoticeActionInput(), updateRebuildableRule: Update);
        Require(stillUnmatched.Contains("Run prepare again", StringComparison.Ordinal) &&
            fx.Store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: false } &&
            fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "check again keeps an unmatched rule's notice open and points at prepare");
        var chosen = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ChooseFolder,
            default, input: new NoticeActionInput("a"), updateRebuildableRule: Update);
        Require(chosen.Contains("Run prepare again", StringComparison.Ordinal) &&
            fx.Store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: true } && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "choose folder updates the rule, closes the notice and changes no file");
        var stored = FirstSyncChoicesState.Load(root, fx.Pair.PairId, fx.Store)!;
        Require(stored.RebuildableChoices!.Single() == new RebuildableChoice("js-packages", "a") && stored.NeedsReprepare,
            "the retained rule names the chosen folder and the review asks for a fresh prepare");
        try
        {
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId,
                """{"rebuildables":[{"id":"js-packages","prefix":"a"}]}""", false, null, _ => { }, CancellationToken.None);
            throw new InvalidOperationException("apply accepted a review that needs a fresh prepare");
        }
        catch (SyncCliReviewException error)
        {
            Require(error.Message.Contains("run prepare again to detect folders under the new prefix", StringComparison.OrdinalIgnoreCase),
                "an apply without a fresh prepare is refused with the reason and option");
        }
        await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None,
            """{"rebuildables":[{"id":"js-packages","prefix":"a"}]}""");
        var applied = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId,
            """{"rebuildables":[{"id":"js-packages","prefix":"a"}]}""", false, null, _ => { }, CancellationToken.None);
        Require(applied.FirstSyncComplete && applied.Failed == 0,
            "the corrected choices apply only after a prepare detects folders under the new prefix");
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey),
            "first-sync completion leaves no unactionable rebuildable notice");

        // Check again closes the notice once the inventory really carries the category's markers.
        var again = Build();
        var againRoot = Path.Combine(root, "again");
        Directory.CreateDirectory(againRoot);
        var againRuntime = new V2CliRuntime(null, again.Store, again.Run(), new EndpointRootProbe(again.Provider), againRoot);
        await againRuntime.PrepareFirstSyncAsync(again.Pair.PairId, _ => { }, CancellationToken.None,
            """{"rebuildables":[{"id":"js-packages","prefix":"typo"}]}""");
        var againNotice = again.Store.OpenNotices(again.Pair.PairId).Single(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey);
        SeedLive(again.Store, again.Pair.A.RootId, Side.A,
            new ItemState("typo/package.json", ItemKind.File, "id:again-marker", "v1", "hash", 2,
                DateTimeOffset.UnixEpoch, ItemFlags.None),
            new ItemState("typo/node_modules", ItemKind.Folder, "id:again-folder", "v1", null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.None));
        var retried = await again.Run().ApplyRetryNoticeChoiceAsync(again.Pair.PairId, againNotice.NoticeId!,
            NoticeChoices.RetryNow, default, input: new NoticeActionInput(),
            updateRebuildableRule: (pair, current, replacement) => V2CliRuntime.UpdateRebuildableRule(againRoot, pair, again.Store, current, replacement));
        Require(retried.Contains("Run prepare again", StringComparison.Ordinal) &&
            again.Store.NoticeById(again.Pair.PairId, againNotice.NoticeId!) is { Closed: true } &&
            again.WriterA.Calls == 0 && again.WriterB.Calls == 0,
            "check again closes the notice when the rule's markers now match, changing no file");

        var remove = Build();
        var removeSource = remove.A.Seed("a/package.json", "{}");
        var removeFolder = remove.A.Seed("a/node_modules", "", ItemKind.Folder);
        var removeRoot = Path.Combine(root, "remove");
        Directory.CreateDirectory(removeRoot);
        var removeRuntime = new V2CliRuntime(null, remove.Store, remove.Run(), new EndpointRootProbe(remove.Provider), removeRoot);
        await removeRuntime.PrepareFirstSyncAsync(remove.Pair.PairId, _ => { }, CancellationToken.None,
            """{"rebuildables":[{"id":"js-packages","prefix":"typo"}]}""");
        var removeNotice = remove.Store.OpenNotices(remove.Pair.PairId).Single(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey);
        var removed = await remove.Run().ApplyRetryNoticeChoiceAsync(remove.Pair.PairId, removeNotice.NoticeId!,
            NoticeChoices.RemoveRule, default, input: new NoticeActionInput(),
            updateRebuildableRule: (pair, current, replacement) => V2CliRuntime.UpdateRebuildableRule(removeRoot, pair, remove.Store, current, replacement));
        Require(removed.Contains("Removed rebuildable rule", StringComparison.Ordinal) &&
            remove.Store.NoticeById(remove.Pair.PairId, removeNotice.NoticeId!) is { Closed: true } &&
            FirstSyncChoicesState.Load(removeRoot, remove.Pair.PairId, remove.Store)!.RebuildableChoices!.Count == 0,
            "remove rule drops only the retained rule and closes the notice");
        Require(remove.WriterA.Calls == 0 && remove.WriterB.Calls == 0,
            "remove rule changes no file before ordinary sync");
        var removeApplied = await removeRuntime.ApplyFirstSyncAsync(remove.Pair.PairId, """{"rebuildables":[]}""",
            false, null, _ => { }, CancellationToken.None);
        Require(removeApplied.FirstSyncComplete && removeApplied.Failed == 0,
            "the apply accepts the corrected choices after Remove rule");
        // Apply performs ordinary first-sync copies to the empty B endpoint. Calls includes Put and MkDir,
        // so assert no effects at removal above, and no destructive effects across both removal and apply here.
        Require(remove.WriterA.Moves == 0 && remove.WriterB.Moves == 0 &&
            remove.WriterA.Deletes == 0 && remove.WriterB.Deletes == 0 &&
            remove.WriterA.Preserves == 0 && remove.WriterB.Preserves == 0,
            "remove rule deletes or moves no file");
        Require(await remove.A.ObserveAsync(removeSource.Path, default) == removeSource &&
            await remove.A.ObserveAsync(removeFolder.Path, default) == removeFolder &&
            await remove.B.ObserveAsync(removeSource.Path, default) is { } copied && copied.Hash == removeSource.Hash &&
            await remove.B.ObserveAsync(removeFolder.Path, default) is { Kind: ItemKind.Folder },
            "ordinary sync after removing the rule retains the source and copies its file and folder");
    }

    // Keep dismisses exactly that rule identity; a changed prefix (or category) is a new rule and may raise again.
    private static void RebuildableKeptRuleSuppression(string root)
    {
        var folder = Path.Combine(root, "keep");
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var notice = new Notice(fx.Pair.PairId, 40, NoticeKind.Info, "typo", "rule", NoticeChoices.RebuildableRuleKey,
            RebuildableRule: new NoticeRebuildableRule("js-packages", "typo"));
        store.AddNotices([notice]);
        var saved = store.OpenNotices(fx.Pair.PairId).Single();
        var run = fx.Run();
        var message = run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, saved.NoticeId!, NoticeChoices.KeepRule, default)
            .GetAwaiter().GetResult();
        Require(message.Contains("will not be raised again", StringComparison.Ordinal) &&
            store.NoticeById(fx.Pair.PairId, saved.NoticeId!) is { Closed: true },
            "Keep dismisses the notice without changing files");
        store.AddNotices([notice]);
        Require(store.OpenNotices(fx.Pair.PairId).Count(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey) == 0,
            "a kept rule is not raised again");
        store.AddNotices([notice with { Path = "other", RebuildableRule = new NoticeRebuildableRule("js-packages", "other") }]);
        Require(store.OpenNotices(fx.Pair.PairId).Count(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey) == 1,
            "a changed rule identity raises a fresh notice");
    }

    // A legacy id embeds the path at write time. Once the item moves, the stored object identity must still find it.
    private static void LegacyMovedRepairChecks(string root)
    {
        var folder = Path.Combine(root, "legacy-moved");
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("moved/new-name.txt", "content");
        var canonical = ShortId.Make("n", fx.Pair.PairId, "31", "provider-name-refused", "A", item.ObjectId);
        var legacy = ShortId.Make("n", fx.Pair.PairId, "31", RowCodec.Key("old-name.txt"),
            "provider-name-refused", "A", item.ObjectId);
        // A database written by an older build arrives with the pre-move row already present: its id and path both
        // name the old location. Seeding it before the first Add mirrors that upgrade path.
        InsertOpenNotice(folder, new Notice(fx.Pair.PairId, 31, NoticeKind.Info, "old-name.txt", "blocked",
            "provider-name-refused", legacy, Repair: new NoticeRepair(Side.A, item, "chosen.txt")));
        var moved = new Notice(fx.Pair.PairId, 31, NoticeKind.Info, item.Path, "blocked", "provider-name-refused",
            Repair: new NoticeRepair(Side.A, item));
        store.AddNotices([moved]);
        var open = store.OpenNotices(fx.Pair.PairId);
        Require(open.Count == 1 && open[0].NoticeId == canonical,
            "a moved item's legacy notice folds into one canonical notice, never a duplicate");
        Require(open[0].Path == item.Path,
            "the folded notice displays the current path even when its detail is unchanged");
        var alias = store.NoticeById(fx.Pair.PairId, legacy);
        Require(alias?.Notice.NoticeId == canonical && alias?.Notice.Repair?.Destination == "chosen.txt",
            "the legacy id resolves to the canonical identity and keeps its pending rename destination");
        var movedAgain = moved with { Path = "moved/again.txt", Repair = new NoticeRepair(Side.A, item with { Path = "moved/again.txt" }) };
        store.AddNotices([movedAgain]);
        var after = store.OpenNotices(fx.Pair.PairId);
        Require(after.Count == 1 && after[0].NoticeId == canonical && after[0].Path == "moved/again.txt" &&
            after[0].Repair?.Destination == "chosen.txt",
            "a second move refreshes one canonical notice without dropping the pending destination");
        var pathOnly = after[0] with { Path = "moved/path-only.txt" };
        store.AddNotices([pathOnly]);
        var refreshed = store.OpenNotices(fx.Pair.PairId);
        Require(refreshed.Count == 1 && refreshed[0].NoticeId == canonical && refreshed[0].Path == pathOnly.Path &&
            NoticeDetailCodec.Encode(refreshed[0]) == NoticeDetailCodec.Encode(after[0]) &&
            store.NoticeById(fx.Pair.PairId, legacy)?.Notice.Path == pathOnly.Path,
            "a path-only refresh keeps one notice, unchanged repair evidence, and the legacy alias");

        // Both rows already open: the canonical row and the legacy row for the same object. The migration must
        // keep exactly one notice and carry the pending destination.
        var bothFolder = Path.Combine(root, "legacy-both");
        using var bothStore = SqliteStateStore.Open(bothFolder, Settings, TimeProvider.System);
        var both = Build(suppliedStore: bothStore);
        var bothItem = both.A.Seed("current/name.txt", "content");
        var bothNotice = new Notice(both.Pair.PairId, 31, NoticeKind.Info, bothItem.Path, "blocked",
            "provider-name-refused", Repair: new NoticeRepair(Side.A, bothItem, "chosen.txt"));
        var bothCanonical = ShortId.Make("n", both.Pair.PairId, "31", "provider-name-refused", "A", bothItem.ObjectId);
        var bothLegacy = ShortId.Make("n", both.Pair.PairId, "31", RowCodec.Key("old/name.txt"),
            "provider-name-refused", "A", bothItem.ObjectId);
        InsertOpenNotice(bothFolder, bothNotice with { NoticeId = bothCanonical });
        InsertOpenNotice(bothFolder, bothNotice with { NoticeId = bothLegacy, Path = "old/name.txt" });
        Require(bothStore.OpenNotices(both.Pair.PairId).Count == 2, "the both-rows fixture starts with two open notices");
        bothStore.AddNotices([bothNotice with { Repair = new NoticeRepair(Side.A, bothItem) }]);
        var folded = bothStore.OpenNotices(both.Pair.PairId);
        Require(folded.Count == 1 && folded[0].NoticeId == bothCanonical && folded[0].Path == bothItem.Path &&
            folded[0].Repair?.Destination == "chosen.txt",
            "an existing canonical row plus a legacy row fold to one notice without losing the destination");
        Require(bothStore.NoticeById(both.Pair.PairId, bothLegacy)?.Notice.NoticeId == bothCanonical,
            "the legacy id still resolves to the canonical identity after both-rows migration");
    }

    // C2/R3: the one-time legacy identity fold scans every open same-reason row with no fixed row window, so more
    // than 64 same-reason notices must not hide a moved item's legacy row.
    private static void LegacyIdentityProbeWindowChecks(string root)
    {
        var folder = Path.Combine(root, "legacy-window");
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("moved/new-name.txt", "content");
        var canonical = ShortId.Make("n", fx.Pair.PairId, "31", "provider-name-refused", "A", item.ObjectId);
        // The stored row still carries the id and path from before the move.
        var legacy = ShortId.Make("n", fx.Pair.PairId, "31", RowCodec.Key("old-name.txt"),
            "provider-name-refused", "A", item.ObjectId);
        InsertOpenNotice(folder, new Notice(fx.Pair.PairId, 31, NoticeKind.Info, "old-name.txt", "blocked",
            "provider-name-refused", legacy, Repair: new NoticeRepair(Side.A, item, "chosen.txt")));
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(folder));
        connection.Open();
        long pairNo;
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT pair_no FROM pair WHERE pair_id=$p;";
            query.Parameters.AddWithValue("$p", fx.Pair.PairId);
            pairNo = (long)query.ExecuteScalar()!;
        }
        // Seventy open same-rule notices; the fold has no row window, so the moved item cannot hide behind them.
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO notice(notice_id,pair_no,rule,kind,path,path_key,detail,reason_key,state,choice,created) " +
                "VALUES($id,$p,31,'Info',$path,$path_key,$detail,'provider-name-refused','open',NULL,0);";
            insert.Parameters.AddWithValue("$p", pairNo);
            var id = insert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var path = insert.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            var pathKey = insert.Parameters.Add("$path_key", Microsoft.Data.Sqlite.SqliteType.Text);
            var detail = insert.Parameters.Add("$detail", Microsoft.Data.Sqlite.SqliteType.Text);
            insert.Prepare();
            for (var i = 0; i < 70; i++)
            {
                var other = new ItemState("other/" + i + ".txt", ItemKind.File, "id:other:" + i, "v1", null, 1,
                    DateTimeOffset.UnixEpoch, ItemFlags.None);
                id.Value = "decoy-" + i.ToString("D2", CultureInfo.InvariantCulture);
                path.Value = other.Path;
                pathKey.Value = PathKey.Of(other.Path);
                detail.Value = NoticeDetailCodec.Encode(new Notice(fx.Pair.PairId, 31, NoticeKind.Info,
                    other.Path, "blocked", "provider-name-refused", Repair: new NoticeRepair(Side.A, other)));
                insert.ExecuteNonQuery();
            }
        }
        Require(store.OpenNotices(fx.Pair.PairId).Count(row => row.ReasonKey == "provider-name-refused") == 71,
            "the fixture holds more than 64 refused-name notices");
        store.AddNotices([new Notice(fx.Pair.PairId, 31, NoticeKind.Info, item.Path, "blocked",
            "provider-name-refused", Repair: new NoticeRepair(Side.A, item))]);
        var open = store.OpenNotices(fx.Pair.PairId);
        Require(open.Count(row => row.Repair?.Item.ObjectId == item.ObjectId) == 1 &&
            open.Single(row => row.Repair?.Item.ObjectId == item.ObjectId).NoticeId == canonical,
            "a moved item beyond 64 same-reason notices folds to exactly one open notice");
        Require(store.NoticeById(fx.Pair.PairId, legacy)?.Notice.NoticeId == canonical &&
            store.NoticeById(fx.Pair.PairId, legacy)?.Notice.Repair?.Destination == "chosen.txt",
            "the legacy id still resolves to the canonical identity and keeps its pending rename destination");
    }

    // R3: the legacy identity fold is a one-time sweep per (pair, reason). After the first add has folded the
    // upgraded rows, later adds pay no probe at all.
    private static void LegacyFoldRunsOnceChecks(string root)
    {
        var folder = Path.Combine(root, "legacy-once");
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("once/name.txt", "content");
        var legacy = ShortId.Make("n", fx.Pair.PairId, "31", RowCodec.Key("older/name.txt"),
            "provider-name-refused", "A", item.ObjectId);
        InsertOpenNotice(folder, new Notice(fx.Pair.PairId, 31, NoticeKind.Info, "older/name.txt", "blocked",
            "provider-name-refused", legacy, Repair: new NoticeRepair(Side.A, item, "chosen.txt")));
        var notice = new Notice(fx.Pair.PairId, 31, NoticeKind.Info, item.Path, "blocked", "provider-name-refused",
            Repair: new NoticeRepair(Side.A, item));
        store.AddNotices([notice]);
        Require(store.LegacyFoldPassesForTest == 1, "the first add runs the one-time legacy identity fold");
        store.AddNotices([notice]);
        Require(store.LegacyFoldPassesForTest == 1, "a later add does no legacy identity probe");
    }

    // Inserts one open notice exactly as an older build wrote it, before the store's first Add runs the fold.
    private static void InsertOpenNotice(string folder, Notice notice)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(folder));
        connection.Open();
        long pairNo;
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT pair_no FROM pair WHERE pair_id=$p;";
            query.Parameters.AddWithValue("$p", notice.PairId);
            pairNo = (long)query.ExecuteScalar()!;
        }
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO notice(notice_id,pair_no,rule,kind,path,path_key,detail,reason_key,state,choice,created) " +
            "VALUES($id,$p,$rule,$kind,$path,$path_key,$detail,$reason,'open',NULL,0);";
        command.Parameters.AddWithValue("$id", notice.NoticeId!);
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$rule", notice.Rule);
        command.Parameters.AddWithValue("$kind", notice.Kind.ToString());
        command.Parameters.AddWithValue("$path", notice.Path);
        command.Parameters.AddWithValue("$path_key", PathKey.Of(notice.Path));
        command.Parameters.AddWithValue("$detail", NoticeDetailCodec.Encode(notice));
        command.Parameters.AddWithValue("$reason", notice.ReasonKey);
        command.ExecuteNonQuery();
    }

    // D1: offer exactly the provider's formats. D9: keep the document's name in the exported file.
    // C6: the same export-notice semantics run on the real SQLite table, not only on the in-memory double.
    private static async Task ExportFormatAndNameChecksAsync()
    {
        await ExportFormatAndNameChecksAsync(null);
        var folder = Path.Combine(Path.GetTempPath(), "export-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
            await ExportFormatAndNameChecksAsync(store);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task ExportFormatAndNameChecksAsync(IStateStore? supplied)
    {
        var cloudItem = new ItemState("Doc.paper", ItemKind.File, "id:doc", "rev:1", null, 10,
            DateTimeOffset.UnixEpoch, ItemFlags.RequiresExport | ItemFlags.Unreadable,
            DropboxExportFacts.Merge(null, "markdown", ["markdown", "html"], true));
        var info = DropboxExportFacts.ForItem(cloudItem);
        Require(info is { DefaultFormat: "markdown" } && info.Formats.SequenceEqual(["markdown", "html"]),
            "provider export_info round-trips through the item's stored identity facts");
        var fx = Build(suppliedStore: supplied);
        fx.Store.AddNotices([new(fx.Pair.PairId, 30, NoticeKind.Info, "Doc.paper", "export", "export-required",
            ExportInfo: info)]);
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single();
        Require(NoticeChoices.Options(notice).Where(option => NoticeChoices.ExportFormat(option.Choice) is not null)
                .Select(option => option.Choice).SequenceEqual([NoticeChoices.ExportMarkdown, NoticeChoices.ExportHtml]),
            "the notice offers exactly the provider's markdown and html formats, never DOCX or PDF");
        fx.Store.AddNotices([notice with { ExportInfo = new NoticeExportInfo(null, []) }]);
        Require(!NoticeChoices.Options(fx.Store.OpenNotices(fx.Pair.PairId).Single())
                .Any(option => NoticeChoices.ExportFormat(option.Choice) is not null),
            "a provider list with no supported format offers no speculative export button");

        // A second pair with the same root ids is refused by pair admission, so the SQLite run needs its own store
        // in its own temporary folder; the in-memory double simply gets another instance.
        var nameFolder = supplied is null
            ? null : Path.Combine(Path.GetTempPath(), "export-sqlite-name-" + Guid.NewGuid().ToString("N"));
        SqliteStateStore? nameStore = null;
        try
        {
            if (nameFolder is not null)
            {
                Directory.CreateDirectory(nameFolder);
                nameStore = SqliteStateStore.Open(nameFolder, Settings, TimeProvider.System);
            }
            var nameFx = Build(suppliedStore: nameStore);
            var paper = nameFx.B.Seed("Folder/Document.paper", "cloud-native") with
            { Flags = ItemFlags.RequiresExport | ItemFlags.Unreadable };
            nameFx.Provider.AddWriter(nameFx.Pair.B, new SignalingWriter(nameFx.WriterB)
            { AfterObserve = item => item?.Path == paper.Path ? item with { Flags = paper.Flags } : item });
            nameFx.Store.AddNotices([new(nameFx.Pair.PairId, 30, NoticeKind.Info, paper.Path, "export", "export-required",
                ExportInfo: new NoticeExportInfo("markdown", ["markdown"]))]);
            var saved = nameFx.Store.OpenNotices(nameFx.Pair.PairId).Single();
            var run = new SyncRun(nameFx.Store, nameFx.Executor, nameFx.Provider, Settings, TimeProvider.System)
            {
                ExportDocumentForTest = (_, _, format, _) =>
                    Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("exported " + format)))
            };
            await run.ApplyRetryNoticeChoiceAsync(nameFx.Pair.PairId, saved.NoticeId!, NoticeChoices.ExportMarkdown, default);
            await run.ApplyRetryNoticeChoiceAsync(nameFx.Pair.PairId, saved.NoticeId!, NoticeChoices.ExportMarkdown, default);
            var destinations = nameFx.Store.ReadSyncRetries(nameFx.Pair.PairId).Select(retry => retry.Path).ToArray();
            Require(destinations.Length == 2 && destinations.All(path =>
                    path.StartsWith("Folder/Document (exported ", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal)) &&
                destinations.Any(path => !path.EndsWith(" 2.md", StringComparison.Ordinal)) &&
                destinations.Any(path => path.EndsWith(" 2.md", StringComparison.Ordinal)) &&
                destinations.All(path => !path.Contains("Export-", StringComparison.Ordinal)),
                "exports keep the document name and gain a unique numeric suffix instead of a GUID name");
        }
        finally
        {
            nameStore?.Dispose();
            if (nameFolder is not null)
            {
                try { Directory.Delete(nameFolder, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // D10: the export effect's metadata identity/rev check runs against the real route shape.
    private static async Task ExportMetadataIdentityChecksAsync()
    {
        using var wire = new DropboxRouteWire();
        using var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new SecretDropboxTokenSource(), "/", binding,
            PilotPath.DropboxNamespaceRootIdentity("root-namespace"));
        var spool = Path.Combine(Path.GetTempPath(), "export-spool-" + Guid.NewGuid().ToString("N"));
        var writer = new DropboxWriter(new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), Side.B,
            client, spool, Settings);
        // The provider returns a file_metadata block whose revision is not the observed one.
        wire.Respond("files/export", HttpStatusCode.OK, bytes: [1, 2, 3],
            apiResult: """{"file_metadata":{"id":"id:doc","rev":"rev:other"}}""");
        var item = new ItemState("/Doc.paper", ItemKind.File, "id:doc", "rev:1", null, 3,
            DateTimeOffset.UnixEpoch, ItemFlags.RequiresExport | ItemFlags.Unreadable);
        try
        {
            await writer.ExportDocumentAsync(item, "markdown", default);
            throw new InvalidOperationException("an export whose revision changed was accepted");
        }
        catch (IOException error)
        {
            Require(error.Message.Contains("changed during export", StringComparison.Ordinal),
                "export refuses a metadata result whose revision does not match the observed document");
        }
        finally
        {
            try { if (Directory.Exists(spool)) Directory.Delete(spool, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Replaces one root's live rows with its current rows plus the given ones (a full snapshot, never a partial one).
    private static void SeedLive(IStateStore store, string rootId, Side side, params ItemState[] items)
    {
        var all = store.Live(rootId).Concat(items)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var gen = store.BeginGeneration(rootId, "test");
        store.WriteSnapshot(new Snapshot(rootId, side, gen, "test-cursor", all, []));
        store.CompleteGeneration(gen, "test-cursor", true);
    }
}
