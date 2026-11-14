using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for SyncedEntries (`--engine-checks syncer`), written by SyncedEntries's writer.</summary>
public static class SyncedEntriesChecks
{
    /// <summary>Checks path keys, exact spellings, side-specific facts, folders and UTC local stamps.</summary>
    public static void Run()
    {
        var modified = new DateTimeOffset(2026, 1, 2, 10, 30, 0, TimeSpan.FromHours(2));
        var utcModified = new DateTimeOffset(2026, 1, 2, 8, 30, 0, TimeSpan.Zero);
        var cases = new[]
        {
            (Local: "Parent/File.txt", Dropbox: "parent/file.txt",
                LocalKey: "PARENT/FILE.TXT", DropboxKey: "PARENT/FILE.TXT", Folder: false),
            (Local: "Parent_/Child_.txt", Dropbox: "Parent./Child?.txt",
                LocalKey: "PARENT_/CHILD_.TXT", DropboxKey: "PARENT./CHILD?.TXT", Folder: false),
            (Local: "Parent_/Child_.txt", Dropbox: "Parent_/Child_.txt",
                LocalKey: "PARENT_/CHILD_.TXT", DropboxKey: "PARENT_/CHILD_.TXT", Folder: false),
            (Local: "Parent/LocalCopy.txt", Dropbox: "Parent/RemoteCopy.txt",
                LocalKey: "PARENT/LOCALCOPY.TXT", DropboxKey: "PARENT/REMOTECOPY.TXT", Folder: false),
            (Local: "Parent/Child_", Dropbox: "Parent/Child:",
                LocalKey: "PARENT/CHILD_", DropboxKey: "PARENT/CHILD:", Folder: true),
            (Local: "", Dropbox: "", LocalKey: "", DropboxKey: "", Folder: true)
        };

        foreach (var item in cases)
        {
            var local = new Entry(item.Local, item.Folder, item.Folder ? 0 : 123,
                item.Folder ? default : modified, "local-id", "", "", null);
            var dropbox = new Entry(item.Dropbox, item.Folder, item.Folder ? 0 : 456,
                item.Folder ? default : modified.AddDays(1), "dropbox-id",
                item.Folder ? "" : "dropbox-revision", item.Folder ? "" : "dropbox-hash", null);
            var expected = new SyncedEntry(item.LocalKey, item.Local, item.DropboxKey, item.Dropbox,
                item.Folder, local.Id, local.Size, item.Folder ? 0 : utcModified.Ticks,
                dropbox.Id, dropbox.ContentHash);

            Check.Require(SyncedEntries.From(local, dropbox) == expected, nameof(SyncedEntries.From));
            Check.Require(SyncedEntries.From(Side.Local, local, dropbox) == expected &&
                SyncedEntries.From(Side.Dropbox, dropbox, local) == expected,
                "The side-taking From orders the entry and its counterpart for either side");

            var changedLocal = local with { Path = "Moved/Local_", Id = "new-local-id", Size = local.Size + 1,
                Modified = local.Modified.AddDays(1) };
            var changedDropbox = dropbox with { Path = "Moved/Remote?", Id = "new-dropbox-id", ContentHash = "new-hash" };
            Check.Require(SyncedEntries.Relink(expected, changedLocal, changedDropbox) == expected with
            {
                LocalPath = changedLocal.Path, LocalKey = PathNames.Key(changedLocal.Path), LocalId = changedLocal.Id,
                DropboxPath = changedDropbox.Path, DropboxKey = PathNames.DropboxKey(changedDropbox.Path), DropboxId = changedDropbox.Id
            }, "Relink changes paths, canonical keys and identities, never saved content stamps or kind.");
        }
    }
}
