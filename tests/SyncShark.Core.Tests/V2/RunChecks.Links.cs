namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task LinkActionChecksAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "link-pages-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var items = Enumerable.Range(0, 57).Select(i => fx.A.Seed($"link-{i:D3}", "target") with { Flags = ItemFlags.Link }).ToArray();
        MappingSnapshot(store, fx.Pair.A, Side.A, items);
        store.AddNotices([new(fx.Pair.PairId, LinkExclusions.Rule, NoticeKind.Info, "", "links", "links-excluded")]);
        var summary = store.OpenNotices(fx.Pair.PairId).Single();
        var run = fx.Run();
        string first = "";
        for (var repeat = 0; repeat < 2; repeat++)
            first = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default);
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "link-item") == 50, "link page is bounded and deduplicated");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default,
            input: new(first.Split("Next cursor (--name): ")[1].Trim()));
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "link-item") == 7, "keyset second page keeps only current links open");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "listing never follows or copies links");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pair-root"));
        var link = Path.Combine(root, "link");
        var outside = new LinkContentCopy.Target(Path.Combine(Path.GetDirectoryName(root)!, "other"), false);
        try { LinkContentCopy.ValidateTarget(root, link, outside, false); throw new InvalidOperationException("Root escape accepted"); }
        catch (IOException error) { Require(error.Message.Contains("confirm", StringComparison.Ordinal) && error.Message.Contains("separately", StringComparison.Ordinal), "outside root refusal has confirmation and alternatives"); }
        try { LinkContentCopy.ValidateTarget(root, link, new(root, true), true, root); throw new InvalidOperationException("Ancestor cycle accepted"); }
        catch (IOException error) { Require(error.Message.Contains("ancestor", StringComparison.Ordinal), "outside confirmation cannot bypass cycle check"); }
        if (OperatingSystem.IsWindows()) await NativeLinkCopyChecksAsync();
    }

    private static async Task NativeLinkCopyChecksAsync()
    {
        var root = LocalChecks.FreshRoot("link-copy-" + Guid.NewGuid().ToString("N"));
        LocalChecks.Seed(root, "target/data.txt", "content");
        var link = Path.Combine(root, "folder-link");
        LocalChecks.CreateJunction(link, Path.Combine(root, "target"));
        var shown = LinkContentCopy.Show(root, link);
        Require(shown.InsideRoot && shown.Path == Path.Combine(root, "target"), "show target resolves junction without copying");
        using var known = new InMemoryStateStore();
        async Task<ItemState> Item(string path) => (await LocalChecks.Inventory(root, known, TimeProvider.System)
            .FullAsync(1, _ => true, null!, default)).Items.Single(i => i.Path == path);
        var definition = LocalChecks.RootOf(root, "root-local");
        var folderItem = await Item("folder-link");
        var copy = Path.Combine(root, "folder-copy");
        using (var prepared = new LinkContentCopy.Prepared(definition, folderItem, false)) await prepared.CopyAsync(copy, default);
        Require(File.ReadAllText(Path.Combine(copy, "data.txt")) == "content" &&
            (File.GetAttributes(copy) & FileAttributes.ReparsePoint) == 0 && Directory.Exists(link), "folder contents copied regularly and link retained");
        using (var prepared = new LinkContentCopy.Prepared(definition, folderItem, false))
        {
            try { await prepared.CopyAsync(copy, default); throw new InvalidOperationException("Folder overwritten"); }
            catch (IOException) { }
        }
        Require(File.ReadAllText(Path.Combine(copy, "data.txt")) == "content", "occupied folder untouched");
        LocalChecks.CreateJunction(Path.Combine(root, "target", "loop"), root);
        try { using var rejected = new LinkContentCopy.Prepared(definition, folderItem, false); throw new InvalidOperationException("Nested link followed"); }
        catch (IOException error) { Require(error.Message.Contains("link", StringComparison.OrdinalIgnoreCase), "nested loop rejected before copying"); }
        LocalChecks.RemoveJunction(Path.Combine(root, "target", "loop"));
        var fileLink = Path.Combine(root, "file-link.txt");
        var symlinkSupported = true;
        try { File.CreateSymbolicLink(fileLink, Path.Combine(root, "target", "data.txt")); }
        catch (UnauthorizedAccessException) { symlinkSupported = false; }
        catch (IOException error) when ((error.HResult & 0xffff) == 1314) { symlinkSupported = false; }
        if (symlinkSupported)
        {
            var fileItem = await Item("file-link.txt");
            var fileCopy = Path.Combine(root, "file-copy.txt");
            using (var prepared = new LinkContentCopy.Prepared(definition, fileItem, false)) await prepared.CopyAsync(fileCopy, default);
            Require(File.ReadAllText(fileCopy) == "content" && new FileInfo(fileLink).LinkTarget is not null, "file copy succeeds without deleting link");
            using (var prepared = new LinkContentCopy.Prepared(definition, fileItem, false))
            {
                try { await prepared.CopyAsync(fileCopy, default); throw new InvalidOperationException("File overwritten"); }
                catch (IOException) { }
            }
            Require(File.ReadAllText(fileCopy) == "content", "occupied file untouched");
        }
        else Console.WriteLine("SKIP file symlink: Windows privilege / Developer Mode unavailable; junction tests still run.");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside-" + Guid.NewGuid().ToString("N"));
        LocalChecks.Seed(outside, "other.txt", "external");
        var outsideLink = Path.Combine(root, "outside-link");
        LocalChecks.CreateJunction(outsideLink, outside);
        var outsideItem = await Item("outside-link");
        Require(!LinkContentCopy.Show(root, outsideLink).InsideRoot, "outside target is explicitly identified");
        try { using var rejected = new LinkContentCopy.Prepared(definition, outsideItem, false); throw new InvalidOperationException("Outside target accepted"); }
        catch (IOException error) { Require(error.Message.Contains("confirm", StringComparison.Ordinal), "native root escape requires explicit confirmation"); }
        using (var prepared = new LinkContentCopy.Prepared(definition, outsideItem, true, confirmedTarget: outside))
            await prepared.CopyAsync(Path.Combine(root, "outside-copy"), default);
        Require(File.ReadAllText(Path.Combine(root, "outside-copy", "other.txt")) == "external", "exact outside confirmation permits bounded copy");
        try { using var rejected = new LinkContentCopy.Prepared(definition, outsideItem, true, confirmedTarget: root); throw new InvalidOperationException("Changed confirmation accepted"); }
        catch (IOException error) { Require(error.Message.Contains("does not match", StringComparison.Ordinal), "confirmation bound to exact target"); }
        try { using var rejected = new LinkContentCopy.Prepared(definition, folderItem, false, requiresConfirmation: _ => true); throw new InvalidOperationException("Exclusion bypassed"); }
        catch (IOException error) { Require(error.Message.Contains("bypasses", StringComparison.Ordinal), "excluded content requires explicit confirmation"); }
        await LinkRunCopyChecksAsync(root, definition, folderItem);
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var partial = Path.Combine(root, LinkContentCopy.Prepared.StagingName());
            var final = Path.Combine(root, "interrupted-" + cancel);
            LinkContentCopy.EntryCopiedForTest = _ => { if (cancel) cancellation.Cancel(); else throw new IOException("injected mid-copy failure"); };
            try
            {
                using var prepared = new LinkContentCopy.Prepared(definition, folderItem, false);
                try { await prepared.CopyAsync(final, cancellation.Token, partial); throw new InvalidOperationException("Interruption ignored"); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            finally { LinkContentCopy.EntryCopiedForTest = null; }
            Require(!Directory.Exists(final) && Directory.Exists(partial), "interruption retains staging, never publishes final");
            var inventory = await LocalChecks.Inventory(root, known, TimeProvider.System).FullAsync(1, _ => true, null!, default);
            Require(inventory.Items.All(i => !i.Path.Contains(Path.GetFileName(partial), StringComparison.Ordinal)), "partial content never enters upload inventory");
        }
        await LinkCopyBoundsAsync();
        using (var oversized = new FileStream(Path.Combine(root, "target", "large.bin"), FileMode.CreateNew))
            oversized.SetLength(LinkContentCopy.MaximumBytes + 1);
        try { using var rejected = new LinkContentCopy.Prepared(definition, folderItem, false); throw new InvalidOperationException("Oversized target accepted"); }
        catch (IOException error) { Require(error.Message.Contains("256 MiB", StringComparison.Ordinal), "oversized folder refused before destination creation"); }
        Require(!Directory.Exists(Path.Combine(root, "oversized-copy")), "oversized preflight writes no destination");
        LocalChecks.RemoveJunction(outsideLink);
        LocalChecks.RemoveJunction(link);
    }

    private static async Task LinkRunCopyChecksAsync(string root, RootDef definition, ItemState item)
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "link-action-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var pair = fx.Pair with { A = definition with { RootId = fx.Pair.A.RootId } };
        store.SavePair(pair);
        MappingSnapshot(store, pair.A, Side.A, item);
        store.AddNotices([new(pair.PairId, LinkExclusions.Rule, NoticeKind.Info, item.Path, "link", "link-item", Repair: new(Side.A, item))]);
        var saved = store.OpenNotices(pair.PairId).Single(n => n.ReasonKey == "link-item");
        var run = fx.Run();
        var shown = await run.ApplyRetryNoticeChoiceAsync(pair.PairId, saved.NoticeId!, NoticeChoices.ShowTarget, default);
        Require(shown.Contains("Target:", StringComparison.Ordinal) && store.ReadSyncRetries(pair.PairId).Count() == 0, "Show target is read-only through Run");
        var result = await run.ApplyRetryNoticeChoiceAsync(pair.PairId, saved.NoticeId!, NoticeChoices.CopyLink, default);
        Require(result.StartsWith("Created regular copy:", StringComparison.Ordinal), "end-to-end link action publishes: " + result);
        var published = store.NoticeById(pair.PairId, saved.NoticeId!)!.Value.Notice.Repair!.Destination!;
        Require(File.ReadAllText(Path.Combine(root, published, "data.txt")) == "content" && store.SyncRetryAt(pair.PairId, published) is not null,
            "only published content receives a sync retry");
        Require((await run.ApplyRetryNoticeChoiceAsync(pair.PairId, saved.NoticeId!, NoticeChoices.CopyLink, default)).Contains("Copy again", StringComparison.Ordinal), "repeat copy offers explicit Copy again");
        LinkContentCopy.EntryCopiedForTest = _ => throw new IOException("injected action failure");
        try { await run.ApplyRetryNoticeChoiceAsync(pair.PairId, saved.NoticeId!, NoticeChoices.CopyLinkAgain, default); }
        finally { LinkContentCopy.EntryCopiedForTest = null; }
        var failed = store.NoticeById(pair.PairId, saved.NoticeId!)!.Value.Notice;
        Require(LocalItems.IsArtifactPath(failed.Repair!.Destination!) && failed.Detail.Contains("Retained excluded staging", StringComparison.Ordinal) &&
            store.ReadSyncRetries(pair.PairId).Count() == 1, "failed action records staging and no upload retry");
        using var cancel = new CancellationTokenSource();
        LinkContentCopy.EntryCopiedForTest = _ => cancel.Cancel();
        try { await run.ApplyRetryNoticeChoiceAsync(pair.PairId, saved.NoticeId!, NoticeChoices.RetryNow, cancel.Token); }
        finally { LinkContentCopy.EntryCopiedForTest = null; }
        var cancelled = store.NoticeById(pair.PairId, saved.NoticeId!)!.Value.Notice;
        Require(cancelled.Repair!.Destination != failed.Repair.Destination && LocalItems.IsArtifactPath(cancelled.Repair.Destination!) &&
            store.ReadSyncRetries(pair.PairId).Count() == 1, "cancelled action records staging and no upload retry");
        var renamed = item with { Path = "renamed-link" };
        MappingSnapshot(store, pair.A, Side.A, renamed);
        store.AddNotices([saved with { Path = renamed.Path, Repair = new(Side.A, renamed) }]);
        var refreshed = store.NoticeById(pair.PairId, saved.NoticeId!)!.Value.Notice;
        Require(refreshed.Path == renamed.Path && refreshed.Repair!.Item.Path == renamed.Path &&
            refreshed.Repair.Destination == cancelled.Repair.Destination, "renamed link reuses entity notice and retains staging repair");
    }

    private static async Task LinkCopyBoundsAsync()
    {
        foreach (var depthLimit in new[] { false, true })
        {
            var root = LocalChecks.FreshRoot("link-limit-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            if (depthLimit)
            {
                var path = target;
                for (var i = 0; i < 65; i++) path = Directory.CreateDirectory(Path.Combine(path, "d")).FullName;
            }
            else for (var i = 0; i < LinkContentCopy.MaximumEntries; i++) File.WriteAllText(Path.Combine(target, "f" + i), "");
            var link = Path.Combine(root, "link");
            LocalChecks.CreateJunction(link, target);
            using var store = new InMemoryStateStore();
            var item = (await LocalChecks.Inventory(root, store, TimeProvider.System).FullAsync(1, _ => true, null!, default)).Items.Single(i => i.Path == "link");
            try { using var prepared = new LinkContentCopy.Prepared(LocalChecks.RootOf(root, "limit-root"), item, false); throw new InvalidOperationException("Bound accepted"); }
            catch (IOException error) { Require(error.Message.Contains("4096 entries or 64", StringComparison.Ordinal), depthLimit ? "depth limit enforced" : "entry limit enforced"); }
            Require(!Directory.EnumerateFileSystemEntries(root).Any(p => SyncLocalTransfer.IsTransferArtifactName(Path.GetFileName(p))), "limit preflight creates no staging");
            LocalChecks.RemoveJunction(link);
        }
    }
}
