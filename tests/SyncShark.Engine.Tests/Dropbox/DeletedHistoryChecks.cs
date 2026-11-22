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
        wire.Add(metadata, native, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        wire.Add(revisions, native, System.Text.Json.JsonSerializer.Serialize(new
        {
            is_deleted = true, server_deleted = "2024-03-02T14:05:06Z",
            entries = new[] { new { id, rev = revision, path_display = native, content_hash = hash } }
        }));
        wire.Add(metadata, id, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        var result = await history.ReadAsync(api, pair, child, false, ct);
        Check.Require(result is DeletionEvidence.DeletedFile deleted && deleted.Path == child && deleted.Domain == "ns-root" &&
            deleted.RemoteId == id && deleted.Revision == revision && deleted.DeletedAt == new DateTimeOffset(2024, 3, 2, 14, 5, 6, TimeSpan.Zero) &&
            deleted.ContentHash == hash && deleted.TimeUnavailable is null && deleted.HashUnavailable is null,
            "Retained deletion lost its matching identity, time or content hash.");
        wire.Done();
    }
}
