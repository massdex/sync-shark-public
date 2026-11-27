using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks endpoint and no-effect semantics through the shared contract.</summary>
public static class SyncActionChecks
{
    public static Task RunAsync()
    {
        var file = new Entry("item", false, 1, DateTimeOffset.UnixEpoch, "id", "rev", "hash", null);
        var row = new Row("item", file, null, null, [], null, RowContext.Neutral);
        var upload = new SyncAction(ActionKind.Upload, row);
        Check.Require(upload.WrittenSide() == Side.Dropbox && !upload.WritesLocal() && upload.CreatesNewItem(), "Upload destination");
        Check.Require(!(upload with { Row = row with { Dropbox = file } }).CreatesNewItem(), "Existing destination is a replace");
        Check.Require(SyncAction.CopyOn(true, Side.Local) == ActionKind.EnsureLocalFolder && SyncAction.DeleteOn(Side.Local) == ActionKind.DeleteLocal, "Local operation mapping");
        Check.Require(new SyncAction(ActionKind.None, row).IsInSync() && !new SyncAction(ActionKind.None, row, NoneReason.Held).IsInSync(), "Held is not synced");
        var both = new SyncAction(ActionKind.KeepBoth, row);
        Check.Require(both.WritesLocal() && (both with { Side = Side.Local }).WritesLocal() &&
            !(both with { Side = Side.Dropbox }).WritesLocal(), "Only a receiving keep-both leg writes locally");
        return Task.CompletedTask;
    }
}
