namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    private static void Cli_ReviewDeletes_EmptyPlanRetainsPreparedRetryEvidence()
    {
        var fx = Build();
        fx.A.OmitVersionIds = true;
        var current = fx.A.Seed("held.txt", "current known content");
        var historical = new PrepareMatch(Content.SideStamp(current with { Hash = null }), "id:peer|v:reviewed");
        var retry = new SyncRetry(current.Path, "sync", 2, "Content proof unavailable", DateTimeOffset.UtcNow.AddHours(1));
        fx.Store.SaveSyncRetry(fx.Pair.PairId, retry);
        var empty = new Plan(fx.Pair.PairId, 0, [], []);
        var run = fx.Run();
        run.DeferPostPreviewChanges(fx.Pair, empty, new Dictionary<string, PrepareMatch> { [current.Path] = historical });
        var retained = fx.Store.SyncRetryAt(fx.Pair.PairId, current.Path);
        Require(retained == (retry with { Match = historical }),
            "an empty-action apply attaches original metadata evidence without changing retry authority or deadline");
        run.DeferPostPreviewChanges(fx.Pair, empty, new Dictionary<string, PrepareMatch>
        { [current.Path] = historical with { A = Content.SideStamp(current) } });
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, current.Path) == retained,
            "a subsequent handoff cannot upgrade historical metadata evidence to a current content hash");
        Console.WriteLine("PASS Cli_ReviewDeletes_EmptyPlanRetainsPreparedRetryEvidence");
    }
}
