using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Exercise label selection and the atomic settlement with two different file versions.</summary>
public static class KeepBothChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair();
        var settings = ItemChecks.Settings with { ComputerName = "Test-PC" };
        var local = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        var a = ItemChecks.File(Side.Local, "report.txt", 1, "a");
        var b = ItemChecks.File(Side.Dropbox, a.Path, 2, "b");
        local.Seed(a, [1]); cloud.Seed(b, [2]);
        using var store = new InMemoryStateStore();
        var row = new Row(a.Path, a, b, null, [], a.ContentHash, RowContext.Neutral);
        await new KeepBoth().ApplyAsync(pair, settings, _ => {}, new(pair, [pair], DriveType.Fixed), row,
            local, cloud, store, (_,_,_,_) => throw new InvalidOperationException(), (_,_,_,_) => throw new InvalidOperationException(), default);
        Check.Require(cloud.Items.Any(x => x.Key.Contains("Test-PC", StringComparison.Ordinal) && x.Value.Bytes.SequenceEqual(new byte[]{1})), "Local version must carry the observed PC name at the Dropbox destination");
        Check.Require(local.Items.Any(x => x.Key.Contains("Dropbox", StringComparison.Ordinal) && x.Value.Bytes.SequenceEqual(new byte[]{2})), "Dropbox version must keep its provider label");
        Check.Require(store.Records.Count == 2 && store.OpenNotices.Single().Subject.Items.Count == 4, "Both links and their notice must settle together");
    }
}
