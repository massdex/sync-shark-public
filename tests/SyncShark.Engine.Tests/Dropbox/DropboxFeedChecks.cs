using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxFeed.</summary>
public static class DropboxFeedChecks
{
    /// <summary>Verifies the DropboxFeed contract through the module surface.</summary>
    public static Task RunAsync()
    {
        // A tombstone keeps its path as an identity-safe hint; a live row carries its id and observed entry.
        var live = new Entry("dir/file", false, 3, DateTimeOffset.UnixEpoch, "id:1", "rev-1", "", null);
        var changes = DropboxFeed.ToChanges([new Listed("dir/file", live), new Listed("dir/gone", null)]);
        Check.Require(changes.Count == 2, "Every decoded entry must become one change.");
        Check.Require(changes[0].Side == Side.Dropbox && changes[0].Kind == ChangeKind.Changed &&
            changes[0].Path == "dir/file" && changes[0].Id == "id:1" && ReferenceEquals(changes[0].Observed, live),
            "A live entry must keep its path, id and observed entry.");
        Check.Require(changes[1].Kind == ChangeKind.Deleted && changes[1].Path == "dir/gone" &&
            changes[1].Id is null && changes[1].Observed is null,
            "A tombstone must be a deletion with no identity or observed entry.");

        // Constant-space continuation cycle detection: an immediate repeat, then a repeat of the retained checkpoint.
        var cycle = new ContinuationCycle();
        Check.Require(new ContinuationCycle().Repeats("same", "same"), "A repeated cursor was not detected.");
        Check.Require(!cycle.Repeats("a", "b") && !cycle.Repeats("b", "c"),
            "A fresh continuation step was reported as a repeat.");
        Check.Require(cycle.Repeats("x", "c"), "A retained checkpoint repeat was not detected.");
        return Task.CompletedTask;
    }
}
