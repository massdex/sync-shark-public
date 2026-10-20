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
        for (var repeat = 0; repeat < 2; repeat++)
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default);
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "link-item") == 50, "link page is bounded and deduplicated");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default, input: new(null, 50));
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "link-item") == 57, "second page includes remaining links");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "listing never follows or copies links");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pair-root"));
        var link = Path.Combine(root, "link");
        var outside = new LinkContentCopy.Target(Path.Combine(Path.GetDirectoryName(root)!, "other"), false);
        try { LinkContentCopy.ValidateTarget(root, link, outside, false); throw new InvalidOperationException("Root escape accepted"); }
        catch (IOException error) { Require(error.Message.Contains("confirm", StringComparison.Ordinal) && error.Message.Contains("separately", StringComparison.Ordinal), "outside root refusal has confirmation and alternatives"); }
        try { LinkContentCopy.ValidateTarget(root, link, new(root, true), true); throw new InvalidOperationException("Ancestor cycle accepted"); }
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
        File.CreateSymbolicLink(fileLink, Path.Combine(root, "target", "data.txt"));
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
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside");
        LocalChecks.Seed(outside, "other.txt", "external");
        var outsideLink = Path.Combine(root, "outside-link");
        LocalChecks.CreateJunction(outsideLink, outside);
        var outsideItem = await Item("outside-link");
        Require(!LinkContentCopy.Show(root, outsideLink).InsideRoot, "outside target is explicitly identified");
        try { using var rejected = new LinkContentCopy.Prepared(definition, outsideItem, false); throw new InvalidOperationException("Outside target accepted"); }
        catch (IOException error) { Require(error.Message.Contains("confirm", StringComparison.Ordinal), "native root escape requires explicit confirmation"); }
        using (var prepared = new LinkContentCopy.Prepared(definition, outsideItem, true))
            await prepared.CopyAsync(Path.Combine(root, "outside-copy"), default);
        Require(File.ReadAllText(Path.Combine(root, "outside-copy", "other.txt")) == "external", "explicit outside confirmation permits bounded copy");
        using (var oversized = new FileStream(Path.Combine(root, "target", "large.bin"), FileMode.CreateNew))
            oversized.SetLength(LinkContentCopy.MaximumBytes + 1);
        try { using var rejected = new LinkContentCopy.Prepared(definition, folderItem, false); throw new InvalidOperationException("Oversized target accepted"); }
        catch (IOException error) { Require(error.Message.Contains("256 MiB", StringComparison.Ordinal), "oversized folder refused before destination creation"); }
        LocalChecks.RemoveJunction(outsideLink);
        LocalChecks.RemoveJunction(link);
    }
}
