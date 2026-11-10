using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task NameMappingAsync(string root)
    {
        _root = root;
        using var h = new Harness();
        h.Wire.SeedFolder("dir");
        h.Wire.SeedFile("dir/a:b.txt", "old");
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        var mapped = snapshot.Items.Single(i => i.Path == "dir/a:b.txt");
        Require(!mapped.Flags.HasFlag(ItemFlags.Unreadable) && mapped.IdentityJson is not null,
            "real Dropbox listing maps forbidden spelling without blocking transfer");
        var writer = h.Writer();
        var current = (await writer.ObserveAsync("dir/a:b.txt", CancellationToken.None))!;
        using (var stream = await writer.OpenReadAsync(current, CancellationToken.None))
        using (var reader = new StreamReader(stream))
            Require(await reader.ReadToEndAsync() == "old", "original colon spelling downloads through provider");
        var bytes = Encoding.UTF8.GetBytes("edited");
        var source = Source("dir/a_b.txt", bytes);
        var action = new SyncAction("mapped-update", 2, ActionKind.Update, Side.B, current.Path, null,
            source, current, false, "mapped");
        var updated = await writer.PutAsync(action, new MemoryStream(bytes), CancellationToken.None);
        Require(updated.Path == "dir/a:b.txt" && updated.ObjectId == current.ObjectId && updated.IdentityJson is not null,
            "conditional upload retains original spelling and entity identity");
        Require(await writer.ObserveAsync("dir/a_b.txt", CancellationToken.None) is null,
            "mapped local name is never uploaded as a different Dropbox item");
        var folder = await writer.MkDirAsync("folder:", CancellationToken.None);
        Require(folder.Path == "folder:", "mapped folder original spelling reaches provider");
        await AuthorizedFixtureDeleteAsync(writer, DeleteAction(updated.Path, updated), CancellationToken.None);
        Require(await writer.ObserveAsync(updated.Path, CancellationToken.None) is null,
            "conditional delete probe accepts original provider spelling");
        foreach (var unsafePath in new[] { "../escape", "dir/../escape", "dir\\escape", "/root" })
        {
            var refused = false;
            try { PilotPath.NormalizeDropboxRelative(unsafePath); } catch (ArgumentException) { refused = true; }
            Require(refused, "provider traversal and backslash paths rejected");
        }
        var localRefused = false;
        try { PilotPath.ResolveContainedPath(root, "dir/a:b.txt"); } catch (ArgumentException) { localRefused = true; }
        Require(localRefused, "local filesystem validator still rejects colon paths");
        Console.WriteLine("PASS Dropbox name mapping provider routes");
    }
}
