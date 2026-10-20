using System.Text;

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
            await ExportFormatAndNameChecksAsync();
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
            [NoticeChoices.PreviewMatches, NoticeChoices.ChooseFolder, NoticeChoices.RemoveRule, NoticeChoices.KeepRule]),
            "the notice offers exactly preview, choose folder, remove and keep");
        var run = fx.Run();
        string Update(PairDef pair, RebuildableChoice current, RebuildableChoice? replacement) =>
            V2CliRuntime.UpdateRebuildableRule(root, pair, fx.Store, current, replacement);
        var preview = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.PreviewMatches,
            default, null, new NoticeActionInput(), Update);
        Require(preview.Contains("b/node_modules", StringComparison.Ordinal) && preview.Contains("No files were", StringComparison.Ordinal) &&
            fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "preview lists bounded candidate matches without any write or deletion");
        var chosen = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ChooseFolder,
            default, null, new NoticeActionInput("a"), Update);
        Require(chosen.Contains("Run prepare again", StringComparison.Ordinal) &&
            fx.Store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: true } && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "choose folder updates the rule, closes the notice and changes no file");
        var stored = FirstSyncChoicesState.Load(root, fx.Pair.PairId, fx.Store)!;
        Require(stored.RebuildableChoices!.Single() == new RebuildableChoice("js-packages", "a"),
            "the retained rule now names the chosen folder");
        var applied = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId,
            """{"rebuildables":[{"id":"js-packages","prefix":"a"}]}""", false, null, _ => { }, CancellationToken.None);
        Require(applied.FirstSyncComplete && applied.Failed == 0,
            "the corrected choices apply under the rewritten review");
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey),
            "first-sync completion leaves no unactionable rebuildable notice");

        var remove = Build();
        remove.A.Seed("a/package.json", "{}");
        remove.A.Seed("a/node_modules", "", ItemKind.Folder);
        var removeRoot = Path.Combine(root, "remove");
        Directory.CreateDirectory(removeRoot);
        var removeRuntime = new V2CliRuntime(null, remove.Store, remove.Run(), new EndpointRootProbe(remove.Provider), removeRoot);
        await removeRuntime.PrepareFirstSyncAsync(remove.Pair.PairId, _ => { }, CancellationToken.None,
            """{"rebuildables":[{"id":"js-packages","prefix":"typo"}]}""");
        var removeNotice = remove.Store.OpenNotices(remove.Pair.PairId).Single(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey);
        var removed = await remove.Run().ApplyRetryNoticeChoiceAsync(remove.Pair.PairId, removeNotice.NoticeId!,
            NoticeChoices.RemoveRule, default, null, new NoticeActionInput(),
            (pair, current, replacement) => V2CliRuntime.UpdateRebuildableRule(removeRoot, pair, remove.Store, current, replacement));
        Require(removed.Contains("Removed rebuildable rule", StringComparison.Ordinal) &&
            remove.Store.NoticeById(remove.Pair.PairId, removeNotice.NoticeId!) is { Closed: true } &&
            FirstSyncChoicesState.Load(removeRoot, remove.Pair.PairId, remove.Store)!.RebuildableChoices!.Count == 0,
            "remove rule drops only the retained rule and closes the notice");
        var removeApplied = await removeRuntime.ApplyFirstSyncAsync(remove.Pair.PairId, """{"rebuildables":[]}""",
            false, null, _ => { }, CancellationToken.None);
        Require(removeApplied.FirstSyncComplete, "the apply accepts the corrected choices after Remove rule");
        Require(remove.WriterA.Calls == 0 && remove.WriterB.Calls == 0, "remove rule deletes or moves no file");
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
        var notice = new Notice(fx.Pair.PairId, 31, NoticeKind.Info, item.Path, "blocked", "provider-name-refused",
            Repair: new NoticeRepair(Side.A, item, "chosen.txt"));
        store.AddNotices([notice]);
        var canonical = store.OpenNotices(fx.Pair.PairId).Single().NoticeId!;
        var legacy = ShortId.Make("n", fx.Pair.PairId, "31", RowCodec.Key("old-name.txt"),
            "provider-name-refused", "A", item.ObjectId);
        // Simulate the pre-move row: its id and path both name the old location.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(folder)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE notice SET notice_id=$old,path=$path WHERE notice_id=$new;";
            command.Parameters.AddWithValue("$old", legacy);
            command.Parameters.AddWithValue("$path", "old-name.txt");
            command.Parameters.AddWithValue("$new", canonical);
            command.ExecuteNonQuery();
        }
        var moved = notice with { Repair = new NoticeRepair(Side.A, item) };
        store.AddNotices([moved]);
        var open = store.OpenNotices(fx.Pair.PairId);
        Require(open.Count == 1 && open[0].NoticeId == canonical && open[0].Path == item.Path,
            "a moved item's legacy notice folds into one canonical notice, never a duplicate");
        var alias = store.NoticeById(fx.Pair.PairId, legacy);
        Require(alias?.Notice.NoticeId == canonical && alias?.Notice.Repair?.Destination == "chosen.txt",
            "the legacy id resolves to the canonical identity and keeps its pending rename destination");
        var movedAgain = moved with { Path = "moved/again.txt", Repair = new NoticeRepair(Side.A, item with { Path = "moved/again.txt" }) };
        store.AddNotices([movedAgain]);
        var after = store.OpenNotices(fx.Pair.PairId);
        Require(after.Count == 1 && after[0].NoticeId == canonical && after[0].Path == "moved/again.txt" &&
            after[0].Repair?.Destination == "chosen.txt",
            "a second move refreshes one canonical notice without dropping the pending destination");

        // Both rows already open: the canonical row and the legacy row for the same object. The migration must
        // keep exactly one notice and carry the pending destination.
        var bothFolder = Path.Combine(root, "legacy-both");
        using var bothStore = SqliteStateStore.Open(bothFolder, Settings, TimeProvider.System);
        var both = Build(suppliedStore: bothStore);
        var bothItem = both.A.Seed("current/name.txt", "content");
        var bothNotice = new Notice(both.Pair.PairId, 31, NoticeKind.Info, bothItem.Path, "blocked",
            "provider-name-refused", Repair: new NoticeRepair(Side.A, bothItem, "chosen.txt"));
        bothStore.AddNotices([bothNotice]);
        var bothCanonical = bothStore.OpenNotices(both.Pair.PairId).Single().NoticeId!;
        var bothLegacy = ShortId.Make("n", both.Pair.PairId, "31", RowCodec.Key("old/name.txt"),
            "provider-name-refused", "A", bothItem.ObjectId);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(bothFolder)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO notice(notice_id,pair_no,rule,kind,path,detail,reason_key,state,choice,created) " +
                "SELECT $old,pair_no,rule,kind,'old/name.txt',detail,reason_key,'open',NULL,created " +
                "FROM notice WHERE notice_id=$new;";
            command.Parameters.AddWithValue("$old", bothLegacy);
            command.Parameters.AddWithValue("$new", bothCanonical);
            command.ExecuteNonQuery();
        }
        Require(bothStore.OpenNotices(both.Pair.PairId).Count == 2, "the both-rows fixture starts with two open notices");
        bothStore.AddNotices([bothNotice with { Repair = new NoticeRepair(Side.A, bothItem) }]);
        var folded = bothStore.OpenNotices(both.Pair.PairId);
        Require(folded.Count == 1 && folded[0].NoticeId == bothCanonical && folded[0].Repair?.Destination == "chosen.txt",
            "an existing canonical row plus a legacy row fold to one notice without losing the destination");
        Require(bothStore.NoticeById(both.Pair.PairId, bothLegacy)?.Notice.NoticeId == bothCanonical,
            "the legacy id still resolves to the canonical identity after both-rows migration");
    }

    // D1: offer exactly the provider's formats. D9: keep the document's name in the exported file.
    private static async Task ExportFormatAndNameChecksAsync()
    {
        var cloudItem = new ItemState("Doc.paper", ItemKind.File, "id:doc", "rev:1", null, 10,
            DateTimeOffset.UnixEpoch, ItemFlags.RequiresExport | ItemFlags.Unreadable,
            DropboxExportFacts.Merge(null, "markdown", ["markdown", "html"], true));
        var info = DropboxExportFacts.ForItem(cloudItem);
        Require(info is { DefaultFormat: "markdown" } && info.Formats.SequenceEqual(["markdown", "html"]),
            "provider export_info round-trips through the item's stored identity facts");
        var fx = Build();
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

        var nameFx = Build();
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
}
