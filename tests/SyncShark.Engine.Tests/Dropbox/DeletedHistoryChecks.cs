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
        // --- Classified history refusal (owned rule; fails on the wrong outcome). ---
        var absent = new DropboxErrors.Classified(TextKey.ReadDropboxFailed, FailureDisposition.Permanent, false, Absent: true);
        Check.Require(DeletedHistory.HistoryRefusal("A/Note.txt", absent) is DeletionEvidence.NoRetainedHistory,
            "An exact structured history absence was not a successful history negative.");
        var refused = new DropboxErrors.Classified(TextKey.ReadDropboxFailed, FailureDisposition.Permanent, false);
        Check.Require(DeletedHistory.HistoryRefusal("A/Note.txt", refused)
                is DeletionEvidence.HistoryUnavailable { Cause: TextKey.ReadDropboxFailed },
            "A history refusal was not kept non-destructive with the classifier's cause.");

        var roots = new RootIdentity("T:\\Pair A", "/Pair A", "connection-a",
            new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = new PairConfig("pair-a", "Pair A", "T:\\Pair A", "/Pair A", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null, roots);

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
