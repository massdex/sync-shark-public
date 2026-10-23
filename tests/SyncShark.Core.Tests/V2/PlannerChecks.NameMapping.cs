using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    public static Task RunNameMappingAsync()
    {
        var names = new[] { ("a:b*c?d\"e<f>g|h", "a_b_c_d_e_f_g_h"), ("dir. /file..  ", "dir__/file____"),
            ("2017-05-22.17:14:03", "2017-05-22.17_14_03") };
        foreach (var (original, local) in names)
        {
            var cloud = CloudName(original);
            Require(cloud.Path == original && cloud.IdentityJson is not null, "mapping is an entity fact; cloud original retained");
            using var json = JsonDocument.Parse(cloud.IdentityJson!);
            Require(json.RootElement.GetProperty("dropboxWindowsPath").GetString() == local, "each forbidden/trailing character maps to underscore");
        }
        foreach (var cloudOnA in new[] { false, true })
        {
            var cloud = CloudName("dir/a:b.txt");
            var local = F("dir/a_b.txt", "local", "h");
            var rows = JoinNames(cloudOnA ? [cloud] : [local], cloudOnA ? [local] : [cloud]);
            var plan = Planner.Decide(PairId, Gen, rows, Opt(true));
            Require(plan.Actions.Count == 1 && plan.Actions[0].Kind == ActionKind.Adopt,
                "equal mapped names adopt without a rename, upload, or delete in either orientation");
            var basis = Base("mapped", cloudOnA ? cloud : local, cloudOnA ? local : cloud);
            var edited = local with { Hash = "edited", Mtime = T0.AddMinutes(1) };
            plan = Planner.Decide(PairId, Gen, JoinNames(cloudOnA ? [cloud] : [edited], cloudOnA ? [edited] : [cloud], [basis]), Opt());
            Require(plan.Actions.Count == 1 && plan.Actions[0] is { Kind: ActionKind.Update, Path: "dir/a:b.txt" },
                "subsequent local edit uploads to original Dropbox spelling");
            plan = Planner.Decide(PairId, Gen, JoinNames(cloudOnA ? [cloud] : [], cloudOnA ? [] : [cloud]), Opt(true));
            Require(plan.Actions.Count == 1 && plan.Actions[0] is { Kind: ActionKind.CopyNew, Path: "dir/a_b.txt" } &&
                plan.Actions[0].Source?.Path == "dir/a:b.txt", "cloud-only mapped download uses local spelling and original source");
        }
        var c = CloudName("dir/a:b.txt");
        var mappedLocal = F("dir/a_b.txt", "local", "h");
        var mappedBasis = Base("mapped", mappedLocal, c);
        var cloudEdit = c with { Hash = "changed", VersionId = "rev2", Mtime = T0.AddMinutes(1) };
        var updatePlan = Planner.Decide(PairId, Gen, JoinNames([mappedLocal], [cloudEdit], [mappedBasis]), Opt());
        Require(updatePlan.Actions.Single() is { Kind: ActionKind.Update, Path: "dir/a_b.txt" }, "cloud edit updates mapped local spelling");
        var movedCloud = cloudEdit with { Path = "dir/new:name.txt", IdentityJson = DropboxNameMapping.Fact("dir/new:name.txt") };
        var movedPlan = Planner.Decide(PairId, Gen, JoinNames([mappedLocal], [movedCloud], [mappedBasis]), Opt());
        Require(movedPlan.Actions.Count == 2 && movedPlan.Actions.All(a => a.Path == "dir/new_name.txt") &&
            movedPlan.Actions.Any(a => a.Kind == ActionKind.Update && a.ExpectedTarget?.Path == "dir/new_name.txt"),
            "mapped cloud move and edit uses mapped local destination for both effects");
        var siblings = JoinNames([mappedLocal with { Hash = "different" }, F("safe.txt", "safe", "h")], [c]);
        var siblingPlan = Planner.Decide(PairId, Gen, siblings, Opt(true));
        Require(siblingPlan.Actions.Any(a => a.Kind == ActionKind.CopyNew && a.Path == "safe.txt") &&
            siblingPlan.Actions.Where(a => a.Path != "safe.txt").All(a => a.Kind == ActionKind.Hold),
            "safe one-sided siblings continue while ambiguous originals stay unchanged");
        var l = F("dir/a_b.txt", "local", "different");
        CheckMappingProtected([l], [c], "a genuine different local underscore file is not merged or removed");
        CheckMappingProtected([l with { Hash = "h" }], [c, CloudName("dir/a?b.txt", "id:second")], "two cloud names mapping to one local path stay separate");
        CheckMappingProtected([l], [c, CloudName("dir/a_b.txt", "id:literal")], "literal cloud underscore spelling prevents mapped join");
        var literalOld = F(mappedLocal.Path, "id:old-literal", "h");
        var occupiedRows = JoinNames([mappedLocal], [c], [Base("literal-link", mappedLocal, literalOld)]);
        var occupiedPlan = Planner.Decide(PairId, Gen, occupiedRows, Opt());
        Require(occupiedPlan.Actions.All(a => a.Kind == ActionKind.Hold) && occupiedPlan.Notices.Count > 0,
            "a local underscore already linked to a different deleted cloud identity is never deleted or reassigned");
        var unknown = l with { Hash = null };
        var pending = JoinNames([unknown], [c]);
        Require(Planner.HashRequests(pending).PathsA.Contains(l.Path), "mapped unknown content requests hash before adoption");
        var pendingPlan = Planner.Decide(PairId, Gen, pending, Opt(true));
        Require(pendingPlan.Actions.All(a => a.Kind == ActionKind.Hold), "unknown mapped content never overwrites either item");
        var folder = CloudName("dir:", kind: PilotEntryKind.Directory);
        var child = F("dir_/new.txt", "new", "new");
        var folderPlan = Planner.Decide(PairId, Gen, JoinNames([D("dir_", "local-dir"), child], [folder]), Opt(true));
        Require(folderPlan.Actions.Any(a => a.Kind == ActionKind.CopyNew && a.Path == "dir:/new.txt"),
            "new local child uploads under original mapped cloud folder");
        var parentA = D("dir", "parent-a");
        var parentB = D("dir", "parent-b");
        var removeFolder = Opt() with { Rebuildables = new RebuildableSelection(Side.B, ["dir"]) };
        var control = Planner.Decide(PairId, Gen, JoinNames([parentA], [parentB], [Base("parent", parentA, parentB)]), removeFolder);
        Require(control.Actions.Any(a => a.Kind == ActionKind.Delete && a.Path == "dir"),
            "ancestor-protection control really plans an enclosing folder deletion");
        var collisionRows = JoinNames([parentA, l], [parentB, c, CloudName("dir/a?b.txt", "other")], [Base("parent", parentA, parentB)]);
        var parentPlan = Planner.Decide(PairId, Gen, collisionRows, removeFolder);
        Require(parentPlan.Actions.Any(a => a.Kind == ActionKind.Hold && a.Path == "dir") &&
            parentPlan.Actions.All(a => a.Kind != ActionKind.Delete), "ancestor deletion cannot expand across an ambiguous mapped child");
        var onlyIn = typeof(SyncRun).GetMethod("OnlyIn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var sides in new[] { (A: new[] { mappedLocal }, B: new[] { c }), (A: new[] { c }, B: new[] { mappedLocal }) })
            Require(((string[])onlyIn.Invoke(null, [sides.A, sides.B])!).Length == 0,
                "mapped presence cannot enter either side's deletion evidence lane");
        Console.WriteLine("PASS Dropbox forbidden-name mapping checks");
        return Task.CompletedTask;
    }

    private static ItemState CloudName(string path, string id = "id:cloud", PilotEntryKind kind = PilotEntryKind.File) =>
        DropboxItems.ToItem(new PilotRemoteEntry(path, kind, id, "rev", 1, "h", T0));

    private static IReadOnlyList<PlanRow> JoinNames(IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b,
        IReadOnlyList<Baseline>? baselines = null) => Planner.Join(baselines ?? [], a, b, Set(), Set(), Complete(), true);

    private static void CheckMappingProtected(IReadOnlyList<ItemState> local, IReadOnlyList<ItemState> cloud, string label)
    {
        var tombstone = local.ToDictionary(i => i.Path, _ => (DateTimeOffset?)T0.AddDays(1));
        var rows = Planner.Join([], local, cloud, Set(), tombstone, Complete(), true);
        var plan = Planner.Decide(PairId, Gen, rows, Opt(true, oneSided: [new("", Side.A, true), new("", Side.B, true)]));
        Require(plan.Actions.All(a => a.Kind == ActionKind.Hold) && plan.Notices.Any(n =>
            n.ReasonKey == "name-mapping-collision" && n.Detail.Contains("Options:", StringComparison.Ordinal)), label);
        Require(rows.Count == local.Count + cloud.Count, "ambiguous entities remain separate rows");
    }
}
