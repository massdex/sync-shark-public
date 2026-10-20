namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task BlockerActionChecksAsync()
    {
        foreach (var name in new[] { "CON", "COM1.txt", "bad:name?.txt", "trailing. ", "<>|", "..." })
            Require(PilotPath.IsWindowsTransferPath(BlockerNames.Suggest(name)), "safe suggestion for " + name);
        foreach (var name in new[] { "../escape", "Other/Child", "CON.txt", "same.txt", "", "bad?name" })
        {
            try { BlockerNames.Destination("same.txt", name); throw new InvalidOperationException("Unsafe name accepted: " + name); }
            catch (SyncRunRefusedException) { }
        }
        var folder = Path.Combine(Path.GetTempPath(), "blocker-actions-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store, displayRoot: true);
        var run = fx.Run();
        var pairId = fx.Pair.PairId;
        var original = fx.A.Seed("blocked.txt", "original");
        await run.PreviewFirstSyncAsync(pairId, new RecordingProgress(), default);
        Notice Refusal(ItemState item) => new(pairId, 31, NoticeKind.Info, item.Path, "Provider refused name", "provider-name-refused",
            Repair: new NoticeRepair(Side.A, item));
        store.AddNotices([Refusal(original)]);
        var notice = store.OpenNotices(pairId).Single(n => n.ReasonKey == "provider-name-refused");
        Require(NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.Rename) &&
            NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.Ignore), "refused name offers real Rename / Keep excluded options");
        Require(store.NoticeById(pairId, notice.NoticeId!)?.Notice.Repair?.Item == original, "repair provenance round trips through SQLite");
        foreach (var name in new[] { "desktop.ini", ".dropbox", "~$document.docx" })
        {
            try
            {
                await run.ApplyRetryNoticeChoiceAsync(pairId, notice.NoticeId!, NoticeChoices.Rename, default, input: new(name));
                throw new InvalidOperationException("Provider-reserved rename was accepted");
            }
            catch (SyncRunRefusedException) { }
        }
        Require(fx.WriterA.Moves == 0, "Dropbox-reserved custom names are rejected before any move");
        fx.B.Seed("occupied.txt", "other");
        try
        {
            await run.ApplyRetryNoticeChoiceAsync(pairId, notice.NoticeId!, NoticeChoices.Rename, default, input: new("occupied.txt"));
            throw new InvalidOperationException("Opposite namespace collision was accepted");
        }
        catch (SyncRunRefusedException) { }
        Require(fx.WriterA.Moves == 0, "opposite namespace collision never moves source");
        fx.A.Seed("local-occupied.txt", "local");
        try
        {
            await run.ApplyRetryNoticeChoiceAsync(pairId, notice.NoticeId!, NoticeChoices.Rename, default, input: new("local-occupied.txt"));
            throw new InvalidOperationException("Source namespace collision was accepted");
        }
        catch (SyncRunRefusedException) { }
        var response = await run.ApplyRetryNoticeChoiceAsync(pairId, notice.NoticeId!, NoticeChoices.Rename, default, input: new("safe.txt"));
        Require(response.Contains("safe.txt", StringComparison.Ordinal) && fx.WriterA.Moves == 1, "explicit custom name runs conditional rename");
        Require(await fx.A.ObserveAsync("blocked.txt", default) is null &&
            (await fx.A.ObserveAsync("safe.txt", default))?.ObjectId == original.ObjectId, "rename preserves exact entity");
        Require(store.NoticeById(pairId, notice.NoticeId!) is { Closed: false }, "rename alone does not close notice");
        store.AddNotices([Refusal(original)]);
        Require(store.NoticeById(pairId, notice.NoticeId!)?.Notice.Repair?.Destination == "safe.txt", "catalog refresh cannot erase pending destination");
        await run.ApplyRetryNoticeChoiceAsync(pairId, notice.NoticeId!, NoticeChoices.Rename, default);
        Require(fx.WriterA.Moves == 1, "repeated rename verifies existing effect without a second move");
        Require(store.SyncRetryAt(pairId, "safe.txt") is not null, "rename schedules targeted re-observation");
        await FirstSyncAsync(run, fx);
        Require(store.NoticeById(pairId, notice.NoticeId!) is { Closed: true }, "notice closes only after renamed identity has synced");
        var reserved = fx.B.Seed("CON", "", ItemKind.Folder);
        for (var i = 0; i < 3; i++) fx.B.Seed("CON/child" + i, "child");
        store.AddNotices([new(pairId, 30, NoticeKind.Info, reserved.Path, "Windows reserved name", "windows-name-invalid",
            Repair: new NoticeRepair(Side.B, reserved))]);
        var invalid = store.OpenNotices(pairId).Single(n => n.Path == reserved.Path);
        await run.ApplyRetryNoticeChoiceAsync(pairId, invalid.NoticeId!, NoticeChoices.Rename, default);
        Require((await fx.B.ObserveAsync("Renamed-CON", default))?.ObjectId == reserved.ObjectId,
            "Windows reserved cloud folder is renamed on its source side with safe suggestion");
        Require(await fx.B.ObserveAsync("Renamed-CON/child2", default) is not null, "folder Rename carries its children");

        var changed = fx.A.Seed("changed.txt", "before");
        store.AddNotices([Refusal(changed)]);
        var stale = store.OpenNotices(pairId).Single(n => n.Path == "changed.txt");
        var updated = fx.A.Seed("changed.txt", "after");
        var refresh = await run.ApplyRetryNoticeChoiceAsync(pairId, stale.NoticeId!, NoticeChoices.Rename, default);
        Require(refresh.Contains("Choose Rename again", StringComparison.Ordinal) && fx.WriterA.Moves == 1,
            "first action refreshes changed revision without moving it");
        Require(store.NoticeById(pairId, stale.NoticeId!)?.Notice.Repair?.Item.VersionId == updated.VersionId,
            "fresh evidence replaces stale revision");
        await run.ApplyRetryNoticeChoiceAsync(pairId, stale.NoticeId!, NoticeChoices.Rename, default);
        Require(fx.WriterA.Moves == 2, "second action renames the refreshed revision");
        var observed = updated with { Path = "another-refused.txt", VersionId = "later" };
        store.AddNotices([Refusal(observed)]);
        // Provider object ids are scoped to their side; the opposite folder can have the same id.
        var entityNotices = store.OpenNotices(pairId)
            .Where(n => n.Repair is { Side: Side.A } repair && repair.Item.ObjectId == updated.ObjectId).ToArray();
        Require(entityNotices.Length == 1 && entityNotices[0].NoticeId == stale.NoticeId,
            "moving to another refused name reuses the entity notice");
        Require(store.NoticeById(pairId, invalid.NoticeId!) is { Closed: false, Notice.Repair.Side: Side.B },
            "reusing a source notice preserves the opposite entity's pending rename notice");
        foreach (var refused in new[] { "desktop.ini", ".dropbox", "~$document.docx" })
            Require(BlockerNames.DropboxRefuses(refused), "provider rejects custom reserved name: " + refused);

        fx.A.Seed("Remaining", "", ItemKind.Folder);
        for (var i = 0; i < 121; i++) fx.A.Seed($"Remaining/{i:D3}.txt", "child");
        store.WriteSnapshot(await fx.A.FullAsync(store.BeginGeneration(fx.Pair.A.RootId, "test"),
            _ => true, SilentProgress, default));
        store.SaveSyncRetry(pairId, new("Remaining/000.txt", "sync", 1, "File is in use", DateTimeOffset.MaxValue));
        store.AddNotices([new(pairId, 40, NoticeKind.Info, "Remaining", "Folder not emptied", "sync-retry", FolderInspection: true)]);
        var remaining = store.OpenNotices(pairId).Single(n => n.Path == "Remaining");
        var page = await run.ApplyRetryNoticeChoiceAsync(pairId, remaining.NoticeId!, NoticeChoices.ShowRemaining, default);
        Require(page.Contains("File is in use", StringComparison.Ordinal) && page.Contains("next offset 50", StringComparison.Ordinal), "first bounded page includes each child's reason");
        Require(!page.Contains("Remaining/050.txt", StringComparison.Ordinal), "first page stops before child 51");
        var second = await run.ApplyRetryNoticeChoiceAsync(pairId, remaining.NoticeId!, NoticeChoices.ShowRemaining, default, input: new(Offset: 50));
        Require(second.Contains("Remaining/050.txt", StringComparison.Ordinal) && !second.Contains("Remaining/000.txt", StringComparison.Ordinal), "second page advances without duplicates");
        Require(store.LiveChildrenPage(fx.Pair.A.RootId, "Remaining", 100, 50).Count == 21, "SQL page limits decoded children");
        Require(store.NoticeById(pairId, remaining.NoticeId!) is { Closed: false } && fx.WriterA.Deletes == 0, "inspection neither deletes nor acknowledges success");
        var host = new V2SyncHost(store, run, new ObservedExecutor(fx.Executor), null!, Settings, TimeProvider.System, Path.Combine(folder, "state.db"));
        var gui = await host.ResolveConflictSelectionCommandAsync([new(new(remaining.NoticeId!, pairId, remaining.Path, 0),
            SyncAuthorityConflictResolutionKind.PreserveBoth, NoticeChoices.ShowRemaining, new(Offset: 100))]);
        Require(gui.Items.Single().Result!.Decisions.Single().Reason.Contains("Remaining/120.txt", StringComparison.Ordinal), "GUI returns shared page result rather than generic acknowledgement");
        var cli = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), folder);
        await cli.ResolveNoticeAsync(pairId, remaining.Path, NoticeChoices.ShowRemaining, remaining.NoticeId, 0, null, default, new(Offset: 100));
        var paper = new Notice(pairId, 30, NoticeKind.Info, "Document.paper", "Export needed", "export-required");
        store.AddNotices([paper]);
        var savedPaper = store.OpenNotices(pairId).Single(n => n.ReasonKey == "export-required");
        var url = await run.ApplyRetryNoticeChoiceAsync(pairId, savedPaper.NoticeId!, NoticeChoices.OpenDropbox, default);
        Require(url == "https://www.dropbox.com/home/b/Document.paper", "Paper navigation returns a URL only");
        Require(store.NoticeById(pairId, savedPaper.NoticeId!) is { Closed: false }, "opening URL is not a successful export");
        Console.WriteLine("PASS bounded blocker actions: names, collision guards, stale identity, idempotent rename, pending notice, folder pages, GUI/CLI and Paper URL");
    }
}
