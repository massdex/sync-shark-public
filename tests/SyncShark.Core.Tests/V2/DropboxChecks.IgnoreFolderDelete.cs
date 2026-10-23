namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task RunIgnoreChecksAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh bounded test directory.");
        Directory.CreateDirectory(root);
        _root = root;
        await FolderTreeChecksAsync();
        await IgnoreFolderDeleteChecksAsync();
        Console.WriteLine("v2 Dropbox ignore and folder-tree checks passed.");
    }

    // Also registered in the full Dropbox suite; no network or executor/planner dependencies.
    public static async Task IgnoreFolderDeleteChecksAsync()
    {
        await IgnoredFolderLeftoversAsync(false);
        await IgnoredFolderLeftoversAsync(true);
        await IgnoredFastDeleteDescendantsAsync();
    }

    private static DropboxWriter IgnoreDeleteWriter(Harness h) => new(Root(), Side.B, h.Client,
        Path.Combine(_root, "spool"),
        V2Settings.Default with { DeleteBatchSize = 1, UploadBatchSize = 1, MkDirBatchSize = 1 },
        [new(SyncIgnoreKind.FileExtension, ".tmp"), new(SyncIgnoreKind.FolderName, "cache")]);

    private static async Task IgnoredFolderLeftoversAsync(bool unexpected)
    {
        using var h = new Harness();
        h.Wire.SeedFolder("small");
        h.Wire.SeedFolder("small/cache");
        h.Wire.SeedFile("small/cache/keep.txt", "ignored through its parent");
        h.Wire.SeedFile("small/scratch.tmp", "ignored extension");
        if (unexpected) h.Wire.SeedFile("small/keep.txt", "must block");
        var writer = IgnoreDeleteWriter(h);
        var folder = await writer.ObserveAsync("small", CancellationToken.None);
        var action = new SyncAction("delete-small", 1, ActionKind.Delete, Side.B, "small", null,
            null, folder, false, null);
        var blocked = false;
        try { await AuthorizedFixtureDeleteAsync(writer, action, CancellationToken.None); }
        catch (ExpectedTargetMismatchException) { blocked = true; }
        Require(blocked == unexpected, "only nonignored children block ordinary folder deletion");
        Require(h.Wire.Calls.Contains("/2/files/delete_v2") == !unexpected,
            "ignored leftovers go with one folder delete, never separate child delete calls");
        Require(!h.Wire.Calls.Contains("/2/files/delete_batch"), "no ignored standalone file deletes");
        Require((await writer.ObserveAsync("small/cache/keep.txt", CancellationToken.None) is not null) == unexpected,
            "ignored subtree travels with its folder, but remains untouched when nonignored content blocks");
    }

    private static async Task IgnoredFastDeleteDescendantsAsync()
    {
        using var rig = await TreeRigAsync(2);
        var writer = IgnoreDeleteWriter(rig.H);
        var folder = rig.Delete.ExpectedTarget!;
        var first = (await writer.ObserveAsync("big/f0.txt", CancellationToken.None))!;
        var second = (await writer.ObserveAsync("big/sub/f1.txt", CancellationToken.None))!;
        var sub = (await writer.ObserveAsync("big/sub", CancellationToken.None))!;
        rig.H.Wire.SeedFile("big/late.tmp", "ignored known file");
        rig.H.Wire.SeedFolder("big/cache");
        rig.H.Wire.SeedFile("big/cache/known.txt", "ignored descendant");
        rig.H.Wire.SeedFile("big/late.txt", "nonignored unexpected file");
        // Known typed observations allow extension and folder-name rules to be applied correctly.
        rig.H.Wire.SeedChangePage(rig.Cursor,
            [rig.H.Wire.ChangeOf("big/late.tmp"), rig.H.Wire.ChangeOf("big/cache"),
             rig.H.Wire.ChangeOf("big/cache/known.txt"), rig.H.Wire.ChangeOf("big/late.txt")],
            false, "ignore-middle");
        rig.H.Wire.FolderDeleteFeedCursor = "ignore-middle";
        // A tombstone-only child under a known ignored folder is also safely ignored.
        rig.H.Wire.BeforeBuryFolder = () => rig.H.Wire.SeedFile("big/cache/during.txt", "during job");
        var tree = new FolderTree(folder, [first, second, sub]) { RevalidateDelete = () => Task.FromResult(true) };
        var outcomes = await writer.DeleteFolderTreesAsync(rig.Cursor, [tree],
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Require(outcomes.Single().Status == FolderTreeStatus.Completed, "ignored additions do not stall fast-delete");
        Require(rig.H.Wire.Restores.SequenceEqual(["big/late.txt"]),
            "only nonignored unexpected descendants are restored");
        Require(await writer.ObserveAsync("big/late.tmp", CancellationToken.None) is null &&
                await writer.ObserveAsync("big/cache/known.txt", CancellationToken.None) is null &&
                await writer.ObserveAsync("big/cache/during.txt", CancellationToken.None) is null,
            "ignored children disappear with the whole folder");
        Require(outcomes.Single().Restored.SequenceEqual(["big/late.txt"]),
            "ignored children produce no restoration notices");
    }
}
