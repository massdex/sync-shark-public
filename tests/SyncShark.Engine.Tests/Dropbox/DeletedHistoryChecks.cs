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
        wire.Done();
    }
}
