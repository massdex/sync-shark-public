using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks the side's staging selection; the upload wire is covered by the A08 scenario.</summary>
public static class DropboxSideChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair();
        var file = ItemChecks.File(Side.Local, "item", 42, "source");
        var folder = ItemChecks.File(Side.Dropbox, file.Path, 0, "folder", true);
        using var bytes = new MemoryStream([42]);
        // An opaque marker: this check only verifies which configured provider reaches the stage owner.
        var api = (DropboxApi)RuntimeHelpers.GetUninitializedObject(typeof(DropboxApi));
        ISide side = new DropboxSide(pair, () => throw new InvalidOperationException(), api, null!,
            (configured, path, input, source, victim, ct) =>
            {
                Check.Require(configured == api && input == bytes && source == file && path == file.Path, "Staged a different source or account");
                return Task.FromResult(victim == folder ? DropboxStaged.Session(path, "session", source.Size, source, source.ContentHash) : DropboxStaged.Single(path, input, source));
            });
        await using var stage = await side.StageAsync(file.Path, bytes, file, folder, default);
        Check.Require(stage is DropboxStaged { SessionId: "session", Content: null } && !stage.ConsumesSourceOnPublish,
            "The selected folder could be removed before its one-byte replacement was in a closed session");
        await using var ordinary = await side.StageAsync(file.Path, bytes, file, null, default);
        Check.Require(ordinary.ConsumesSourceOnPublish, "Invented a folder victim for a free target");
        var same = await side.ReadChangesAsync("cursor", null, ("cursor", null), default);
        Check.Require(same.CaughtUp && !same.ContinuityLost && same.Changes.Count == 0, "Equal boundaries must finish without a read");
    }
}
