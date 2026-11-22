using System.Net;
using System.Text;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxListing.</summary>
public static class DropboxListingChecks
{
    /// <summary>Verifies the DropboxListing contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        // --- Spelling and exact-name outcomes (owned logic; fails first on a wrong outcome). ---
        Check.Require(DropboxListing.Respelled(FileEntry("/Parent/Old.txt"), "Folder/New.txt").Path == "Folder/Old.txt",
            "Respelled took a stale parent instead of the requested parent.");
        Check.Require(DropboxListing.Respelled(FileEntry("/x/y/C.txt"), "A/B/C.txt").Path == "A/B/C.txt",
            "Respelled did not rebuild the full requested path.");

        // Exact-name: the decoded key must equal the requested key; the actual leaf spelling is kept.
        var same = new Listed("A/Note.txt", FileEntry("/x/A/Note.txt"));
        Check.Require(DropboxListing.ExactOutcome(same, "A/Note.txt").Path == "A/Note.txt",
            "ExactOutcome rejected a matching key.");
        var different =
            new Listed("B/Note.txt", FileEntry("/x/B/Note.txt"));
        try
        {
            _ = DropboxListing.ExactOutcome(different, "A/Note.txt");
            Check.Require(false, "ExactOutcome accepted a different in-pair key.");
        }
        catch (ItemFailedException) { }
        try
        {
            _ = DropboxListing.ExactOutcome(new Listed("A/Note.txt", null), "A/Note.txt");
            Check.Require(false, "ExactOutcome accepted a tombstone reply.");
        }
        catch (ItemFailedException) { }

        // By-ID: preserve the actual location, including outside the pair; refuse a mismatched identity.
        var roots = new RootIdentity("T:\\Pair A", "/Pair A", "connection-a",
            new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = new PairConfig("pair-a", "Pair A", "T:\\Pair A", "/Pair A", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null, roots);
        var at = DropboxListing.VerifiedById(
            new Entry("Pair A/Child/Note.txt", false, 1, DateTimeOffset.UnixEpoch, "id:inside", "rev-1", "", null),
            "id:inside", pair);
        Check.Require(at.Domain == "ns-root" && at.NativePath == "/Pair A/Child/Note.txt" &&
            at.Entry.Path == "Child/Note.txt", "VerifiedById mapped an in-pair identity wrongly.");
        var far = DropboxListing.VerifiedById(
            new Entry("Other/x.txt", false, 1, DateTimeOffset.UnixEpoch, "id:outside", "rev-2", "", null),
            "id:outside", pair);
        Check.Require(far.NativePath == "/Other/x.txt" && far.Entry.Path == "/Other/x.txt",
            "VerifiedById discarded a location outside the pair.");
        try
        {
            _ = DropboxListing.VerifiedById(
                new Entry("Pair A/Child/Note.txt", false, 1, DateTimeOffset.UnixEpoch, "id:inside", "rev-1", "", null),
                "different", pair);
            Check.Require(false, "VerifiedById accepted a mismatched identity.");
        }
        catch (ItemFailedException) { }

        // --- Real read operations (exercise the I/O path; a stubbed transport reports NOT IMPLEMENTED). ---
        // Each call below drives the actual reader; on the current W09/W10/W11 stubs it throws NotImplementedException.
        var api = DropboxCheckApi.ForStatus(HttpStatusCode.InternalServerError);
        var listing = new DropboxListing();
        try
        {
            _ = await listing.ReadOneAsync(api, pair, "A/Note.txt", CancellationToken.None);
            Check.Require(false, "An exact-name server error fabricated a result instead of failing.");
        }
        catch (ItemFailedException) { }
        try
        {
            _ = await listing.ReadByIdAsync(api, pair, "id:inside", CancellationToken.None);
            Check.Require(false, "A by-id server error fabricated a result instead of failing.");
        }
        catch (ItemFailedException) { }
        try
        {
            await foreach (var _ in listing.ReadFolderAsync(api, pair, "id:folder",
                new ListOptions(true, false), CancellationToken.None)) { }
            Check.Require(false, "A folder server error fabricated a listing instead of failing.");
        }
        catch (InventoryIncompleteException) { }
        try
        {
            _ = await listing.ReadChangePageAsync(api, pair, "cursor", CancellationToken.None);
            Check.Require(false, "A change-page server error fabricated a page instead of failing.");
        }
        catch (InventoryIncompleteException) { }
    }

    private static Entry FileEntry(string path) =>
        new(path, false, 1, DateTimeOffset.UnixEpoch, "id", "rev", "", null);
}

/// <summary>Builds a DropboxApi over a stub transport so a check can drive the real read path at F1.</summary>
internal static class DropboxCheckApi
{
    public static DropboxApi ForStatus(HttpStatusCode status) =>
        new(new HttpClient(new DropboxStubHandler(status)), TimeProvider.System, SettingsParser.Load(),
            static _ => { }, static _ => ValueTask.FromResult<string?>("test-token"), "ns-root");
}

/// <summary>Answers every request with one status and a small refusal body; the real DropboxApi owns auth at F1.</summary>
internal sealed class DropboxStubHandler(HttpStatusCode status) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error_summary\":\"other\",\"error\":{\".tag\":\"other\"}}",
                Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
