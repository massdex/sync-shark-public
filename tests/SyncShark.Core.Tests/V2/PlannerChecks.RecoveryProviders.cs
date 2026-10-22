namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void EqualRevision_ProviderMatrix()
    {
        foreach (var (providerA, providerB) in new (string?, string?)[]
        {
            (SyncProviders.Local, SyncProviders.Dropbox),
            (SyncProviders.Dropbox, SyncProviders.Local),
            (SyncProviders.Local, SyncProviders.Local),
            (SyncProviders.Dropbox, SyncProviders.Dropbox),
            (SyncProviders.Local, null),
            (null, SyncProviders.Dropbox),
            (null, null),
            (SyncProviders.Local, SyncProviders.GoogleDrive),
            (SyncProviders.GoogleDrive, SyncProviders.Dropbox),
            (SyncProviders.Local, "Dropbox"),
            ("Dropbox", SyncProviders.Local)
        })
        foreach (var target in new[] { Side.A, Side.B })
        foreach (var baseline in new[] { true, false })
        {
            var a = F("x", "idA", "same");
            var b = F("x", "idB", "same");
            var evidence = new Evidence(target == Side.B, target == Side.A, true, true,
                T0.AddHours(1), T0.AddHours(1), "same", "same");
            var row = Row(baseline ? Base("k1", a, b) : null,
                target == Side.A ? a : null, target == Side.B ? b : null, evidence);
            var options = Opt(!baseline) with { ProviderA = providerA, ProviderB = providerB };
            var plan = Planner.Decide(PairId, Gen, [row], options);
            var localTargetWithDropboxHistory = target == Side.A
                ? string.Equals(providerA, SyncProviders.Local, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(providerB, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase)
                : string.Equals(providerB, SyncProviders.Local, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(providerA, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase);
            AssertActions(plan, new Exp(ActionKind.Delete, target, "x", null,
                !localTargetWithDropboxHistory, baseline ? 6 : 19, Key: baseline ? "k1" : null));
            AssertEmptyNotices(plan);
        }

        // Rule 19's folder delete is collapsed by the rule-23 postpass. Folders never match a
        // deleted revision, so the final delete keeps Recovery (contents are not covered by history).
        var folder = D("dir", "idA");
        var folderRow = Row(null, folder, null, Ev(B: true, atB: T0.AddHours(1)));
        var folderPlan = Planner.Decide(PairId, Gen, [folderRow], Opt(firstSync: true));
        AssertActions(folderPlan, new Exp(ActionKind.Delete, Side.A, "dir", null, true, 23));
        AssertEmptyNotices(folderPlan);
    }
}
