using System.Net;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DeletedHistory.</summary>
public static class DeletedHistoryChecks
{
    /// <summary>Verifies the DeletedHistory contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var roots = new RootIdentity("T:\\Pair A", "/Pair A", "connection-a",
            new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = new PairConfig("pair-a", "Pair A", "T:\\Pair A", "/Pair A", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null, roots);

        using var wire = new DropboxStubHandler();
        var api = wire.Client();
        var history = new DeletedHistory();
        const string child = "A/Child", native = "/Pair A/A/Child";
        const string metadata = "files/get_metadata", revisions = "files/list_revisions";
        var ct = CancellationToken.None;
        wire.Add(metadata, native, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(metadata, "/Pair A/A", """{".tag":"deleted","path_display":"/Pair A/A"}""");
        Check.Require(await history.ReadAsync(api, pair, child, true, ct) is DeletionEvidence.DeletedPath,
            "Folder ancestor tombstone was not recognized.");
        wire.Add(metadata, native, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(revisions, native, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        Check.Require(await history.ReadAsync(api, pair, child, false, ct) is DeletionEvidence.NoRetainedHistory,
            "Exact missing revision history was not distinguished from a refusal.");
        wire.Add(metadata, native, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(revisions, native, "{}", HttpStatusCode.Forbidden);
        Check.Require(await history.ReadAsync(api, pair, child, false, ct) is DeletionEvidence.HistoryUnavailable,
            "A refused history was mistaken for evidence.");
        const string id = "id:retained", revision = "rev-a";
        var hash = new string('a', 64);
        var head = System.Text.Json.JsonSerializer.Serialize(new
        {
            is_deleted = true, server_deleted = "2024-03-02T14:05:06Z",
            entries = new[] { new { id, rev = revision, path_display = native, content_hash = hash } }
        });
        wire.Add(metadata, native, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(revisions, native, head);
        wire.Add(metadata, id, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        var result = await history.ReadAsync(api, pair, child, false, ct);
        Check.Require(result is DeletionEvidence.DeletedFile deleted && deleted.Path == child && deleted.Domain == "ns-root" &&
            deleted.RemoteId == id && deleted.Revision == revision && deleted.DeletedAt == new DateTimeOffset(2024, 3, 2, 14, 5, 6, TimeSpan.Zero) &&
            deleted.ContentHash == hash && deleted.TimeUnavailable is null && deleted.HashUnavailable is null,
            "Retained deletion lost its matching identity, time or content hash.");
        const string moved = "/Pair A/B/Moved";
        wire.Add(metadata, native, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(revisions, native, head);
        wire.Add(metadata, id, """{".tag":"file","path_display":"/Pair A/B/Moved","id":"id:retained","rev":"rev-b","size":7,"client_modified":"2024-03-03T14:05:06Z"}""");
        var current = await history.ReadAsync(api, pair, child, false, ct);
        Check.Require(current is DeletionEvidence.LiveElsewhere { Path: child, Location: { Domain: "ns-root", NativePath: moved,
            Entry: { Path: "B/Moved", Id: id, Revision: "rev-b", IsFolder: false } } },
            "A live revision identity elsewhere was not returned at its current location.");
        const string nested = "A/B/Child";
        // (a) Tombstone at the exact path: exactly one get_metadata, no ancestor probe.
        wire.Add(metadata, native, """{".tag":"deleted","path_display":"/Pair A/A/Child"}""");
        Check.Require(await history.ReadAsync(api, pair, child, true, ct) is DeletionEvidence.DeletedPath { Path: child },
            "An exact-path folder tombstone must cost exactly one metadata read.");
        // (b) Tombstone two levels up: exactly the path, parent and grandparent, then DeletedPath.
        wire.Add(metadata, "/Pair A/A/B/Child", DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(metadata, "/Pair A/A/B", DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(metadata, "/Pair A/A", """{".tag":"deleted","path_display":"/Pair A/A"}""");
        Check.Require(await history.ReadAsync(api, pair, nested, true, ct) is DeletionEvidence.DeletedPath { Path: nested },
            "A two-level folder tombstone must cost exactly one metadata read per ancestor.");
        wire.Done();
    }
}
