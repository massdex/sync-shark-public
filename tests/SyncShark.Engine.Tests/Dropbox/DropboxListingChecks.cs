using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxListing.</summary>
public static class DropboxListingChecks
{
    /// <summary>Verifies the DropboxListing contract through the module surface.</summary>
    public static Task RunAsync()
    {
        var roots = new RootIdentity("T:\\Pair A", "/Pair A", "connection-a",
            new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = new PairConfig("pair-a", "Pair A", "T:\\Pair A", "/Pair A", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null, roots);

        // Respelled takes the requested path's parent and the reply's own leaf name: one correction for all
        // single-item replies, so an exact-name read never adopts a stale ancestor spelling.
        Check.Require(DropboxListing.Respelled(FileEntry("/Parent/Old.txt"), "Folder/New.txt").Path == "Folder/Old.txt",
            "Respelled took a stale parent instead of the requested parent.");
        Check.Require(DropboxListing.Respelled(FileEntry("/x/y/C.txt"), "A/B/C.txt").Path == "A/B/C.txt",
            "Respelled did not rebuild the full requested path.");
        Check.Require(DropboxListing.Respelled(FileEntry("/anything"), "").Path == "anything",
            "Respelled lost the leaf name at the pair root.");

        // VerifiedById preserves the actual id and the actual native location, including outside this pair.
        var inside = new Entry("Pair A/Child/Note.txt", false, 1, DateTimeOffset.UnixEpoch, "id:inside", "rev-1", "", null);
        var at = DropboxListing.VerifiedById(inside, "id:inside", pair);
        Check.Require(at.Domain == "ns-root" && at.NativePath == "/Pair A/Child/Note.txt" &&
            at.Entry.Path == "Child/Note.txt" && at.Entry.Id == "id:inside",
            "VerifiedById mapped an in-pair identity to the wrong native location or relative path.");

        var elsewhere = new Entry("Other/x.txt", false, 1, DateTimeOffset.UnixEpoch, "id:outside", "rev-2", "", null);
        var far = DropboxListing.VerifiedById(elsewhere, "id:outside", pair);
        Check.Require(far.Domain == "ns-root" && far.NativePath == "/Other/x.txt" && far.Entry.Path == "/Other/x.txt",
            "VerifiedById discarded a location outside the pair instead of preserving it.");

        try
        {
            _ = DropboxListing.VerifiedById(inside, "different", pair);
            Check.Require(false, "VerifiedById accepted a mismatched identity.");
        }
        catch (ItemFailedException) { }

        var unbound = pair with { Roots = null };
        try
        {
            _ = DropboxListing.VerifiedById(inside, "id:inside", unbound);
            Check.Require(false, "VerifiedById accepted an unbound root.");
        }
        catch (EngineRequestException error)
        {
            Check.Require(error.Key == TextKey.RootUnbound, "A missing root names the root-unbound cause.");
        }
        return Task.CompletedTask;
    }

    private static Entry FileEntry(string path) =>
        new(path, false, 1, DateTimeOffset.UnixEpoch, "id", "rev", "", null);
}
