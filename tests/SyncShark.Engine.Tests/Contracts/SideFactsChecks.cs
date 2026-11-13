using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for the saved-entry, observation and keep-both side selectors (`--engine-checks side-facts`).</summary>
public static class SideFactsChecks
{
    internal static Entry? EntrySink;
    internal static string? StringSink;
    internal static bool BoolSink;

    public static void Run()
    {
        var saved = new SyncedEntry("LocalKey", "SavedLocal.txt", "DropboxKey", "SavedRemote.txt", false,
            "local-id", 1, 0, "dropbox-id", "content-hash");
        var local = MakeEntry("LocalAtSynced.txt");
        var dropbox = MakeEntry("DropboxAtSynced.txt");
        var observations = new Observations(local, dropbox, true, false, true, false,
            "content-hash", "LocalDestination", "DropboxDestination");

        Check.Require(saved.PathOn(Side.Local) == saved.LocalPath &&
            saved.PathOn(Side.Dropbox) == saved.DropboxPath, "SyncedEntry.PathOn selects the stored path by side.");
        Check.Require(saved.PathOn((Side)7) == saved.DropboxPath,
            "SyncedEntry.PathOn falls back to the Dropbox path.");
        Check.Require(saved.KeyOn(Side.Local) == saved.LocalKey &&
            saved.KeyOn(Side.Dropbox) == saved.DropboxKey, "SyncedEntry.KeyOn selects the stored key by side.");
        Check.Require(saved.KeyOn((Side)7) == saved.DropboxKey,
            "SyncedEntry.KeyOn falls back to the Dropbox key.");

        Check.Require(ReferenceEquals(observations.AtSynced(Side.Local), local),
            "Observations.AtSynced(Local) is the local observation.");
        Check.Require(ReferenceEquals(observations.AtSynced(Side.Dropbox), dropbox),
            "Observations.AtSynced(Dropbox) is the Dropbox observation.");
        Check.Require(ReferenceEquals(observations.AtSynced((Side)7), dropbox),
            "Observations.AtSynced falls back to Dropbox.");
        Check.Require(new Observations(null, dropbox, false, false, false, false, null, null, null)
            .AtSynced(Side.Local) is null, "Observations.AtSynced has no counterpart fallback.");

        var labels = new KeepBothLabels("RemoteCopy", true, "LocalCopy", false);
        var onLocal = labels.On(Side.Local);
        Check.Require(onLocal.Label == labels.LocalLabel && onLocal.Reused == labels.ReuseLocal,
            "KeepBothLabels.On(Local) is the local label and reuse fact.");
        var onDropbox = labels.On(Side.Dropbox);
        Check.Require(onDropbox.Label == labels.DropboxLabel && onDropbox.Reused == labels.ReuseDropbox,
            "KeepBothLabels.On(Dropbox) is the Dropbox label and reuse fact.");
        Check.Require(labels.On((Side)7).Label == labels.DropboxLabel,
            "KeepBothLabels.On falls back to Dropbox.");
        Check.Require(new KeepBothLabels("RemoteCopy", true, "LocalCopy", false) == labels,
            "KeepBothLabels keeps its record equality.");
        Check.Require(labels with { ReuseLocal = true } != labels,
            "a changed reuse fact changes equality.");
        Check.Require(typeof(KeepBothLabels).Namespace == "Massdex.SyncShark.Engine",
            "KeepBothLabels lives at the engine root.");

        SelectorsAllocateNothing(saved, observations, labels);
    }

    private static void SelectorsAllocateNothing(SyncedEntry saved, Observations observations,
        KeepBothLabels labels)
    {
        Check.Require(
            AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(saved.PathOn(Side.Local));
                    Sink(saved.PathOn(Side.Dropbox));
                    Sink(saved.KeyOn(Side.Local));
                    Sink(saved.KeyOn(Side.Dropbox));
                }
            }) == AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(saved.LocalPath);
                    Sink(saved.DropboxPath);
                    Sink(saved.LocalKey);
                    Sink(saved.DropboxKey);
                }
            }), "saved path and key selectors allocate no more than their inline expressions.");

        Check.Require(
            AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(observations.AtSynced(Side.Local));
                    Sink(observations.AtSynced(Side.Dropbox));
                }
            }) == AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(observations.LocalAtSynced);
                    Sink(observations.DropboxAtSynced);
                }
            }), "Observations.AtSynced allocates no more than its inline expression.");

        Check.Require(
            AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    var local = labels.On(Side.Local);
                    Sink(local.Label);
                    Sink(local.Reused);
                    var dropbox = labels.On(Side.Dropbox);
                    Sink(dropbox.Label);
                    Sink(dropbox.Reused);
                }
            }) == AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    Sink(labels.LocalLabel);
                    Sink(labels.ReuseLocal);
                    Sink(labels.DropboxLabel);
                    Sink(labels.ReuseDropbox);
                }
            }), "KeepBothLabels.On allocates no more than its inline expressions.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(Entry? value) => EntrySink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(string? value) => StringSink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value) => BoolSink = value;

    private static Entry MakeEntry(string path) =>
        new(path, false, 0, DateTimeOffset.UnixEpoch, "id", "rev", "hash", null);
}
