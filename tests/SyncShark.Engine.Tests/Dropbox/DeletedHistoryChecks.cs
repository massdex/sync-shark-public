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

        // Real read operations: a failed history read must stay non-destructive for both a file and a folder
        // candidate.  On the current W09/W10/W11 stubs the DropboxApi constructor reports NOT IMPLEMENTED; the
        // folder-tombstone-versus-file case is listed for proof at F1 in REPORT.txt.
        var api = DropboxCheckApi.ForStatus(HttpStatusCode.InternalServerError);
        var history = new DeletedHistory();
        var file = await history.ReadAsync(api, pair, "A/Note.txt", CancellationToken.None);
        Check.Require(file is DeletionEvidence.Unknown,
            "A file-history server error produced deletion evidence instead of a non-destructive unknown.");
        var folder = await history.ReadAsync(api, pair, "A/Folder", true, CancellationToken.None);
        Check.Require(folder is DeletionEvidence.Unknown,
            "A folder-history server error produced deletion evidence instead of a non-destructive unknown.");
    }
}
