namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    // d3: the latest report of each kind is kept per pair, so the status surface has live progress without the store.
    private static async Task LatestProgressPerPairAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.MonitorProgressScript = progress => progress.Report(new RunProgress(RunPhases.Selecting, null, 0, 0));
        rig.Run.ProgressScript = progress =>
        {
            progress.Report(new RunProgress(RunPhases.Applying, "a.txt", 3, 10));
            progress.Report(new RunProgress(RunPhases.Applying, "b.txt", 7, 10));
        };
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Idle);
        var latest = rig.Auto.Progress["p1"];
        Require(latest.Kind == LiveProgress.AutoSyncKind && latest.Phase == RunPhases.Applying &&
            latest.Done == 7 && latest.Total == 10 && latest.Path == "b.txt",
            "the incremental run's latest report is kept per pair with its kind");
        rig.Run.ProgressScript = null;
        rig.Clock.Advance(SafetyPoll);
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Auto.Progress["p1"].Kind == LiveProgress.ReconciliationKind &&
            rig.Auto.Progress["p1"].Phase == RunPhases.Selecting,
            "monitoring progress is kept as the reconciliation kind when the incremental run reports none");
        Pass("LatestProgressPerPair");
    }
}
