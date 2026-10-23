namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    public static Task ConflictChoiceChecksAsync()
    {
        var baseline = Base("shared", F("file.txt", "a", "base", versionId: "base-a"),
            F("file.txt", "b", "base", versionId: "base-b"));
        var a = F("file.txt", "a", "edit-a", versionId: "edit-a");
        var b = F("file.txt", "b", "edit-b", versionId: "edit-b");
        var plan = Decide(Row(baseline, a, b));
        var notice = plan.Notices.Single();
        Require(plan.Actions.Count(action => action.Kind == ActionKind.KeepBoth) == 2 &&
            plan.Actions.Count(action => action.Kind == ActionKind.Forget) == 1,
            "rule27 preserves originals and both cross-side conflict copies");
        Require(notice.ConflictVersions?.Base == baseline && notice.ConflictVersions.A == a && notice.ConflictVersions.B == b,
            "rule27 retains exact shared revision and original identities before Forget");
        Require(notice.ConflictVersions!.CopyOnA == plan.Actions.Single(action => action.Target == Side.A).Path &&
            notice.ConflictVersions.CopyOnB == plan.Actions.Single(action => action.Kind == ActionKind.KeepBoth && action.Target == Side.B).Path,
            "copy provenance contains actual generated names");
        Require(NoticeOptions.Options(notice).Select(option => option.Choice).SequenceEqual(
            new[] { "compare", "merge", "keep-a", "keep-b", "keep-both" }), "rule27 exposes every explicit choice");
        var roundTrip = NoticeDetailCodec.Decode(notice with { Detail = NoticeDetailCodec.Encode(notice), ConflictVersions = null });
        Require(roundTrip.ConflictVersions == notice.ConflictVersions, "baseline and copy identities persist through notice codec");
        foreach (var bothMoved in new[] { false, true })
        {
            var left = a with { Path = "moved-a.txt", Hash = "base" };
            var right = bothMoved ? b with { Path = "moved-b.txt", Hash = "base" } : b;
            var places = Decide(Row(baseline, left, right));
            Require(places.Actions.Count == 2 && places.Actions.All(action => action.Kind == ActionKind.CopyNew),
                "rules10/11 still copy both locations");
            Require(places.Notices.Single().ConflictVersions is { } versions && versions.A == left && versions.B == right,
                "rules10/11 choices bind exact original locations");
            Require(NoticeOptions.Options(places.Notices.Single()).Select(option => option.Choice).SequenceEqual(
                new[] { "keep-a", "keep-b", "keep-both" }), "rules10/11 expose all location choices");
        }
        var mover = Row(baseline, a with { Path = "occupied.txt", Hash = "base" }, baseline.B);
        var occupant = Row(null, null, F("occupied.txt", "unrelated", "other"));
        var collision = Decide(false, mover, occupant);
        Require(collision.Actions.Count == 0, "rule14 neither moves nor creates an automatic copy or mirror");
        var held = collision.Notices.Single();
        Require(held.Rule == 14 && held.Kind == NoticeKind.UserDecision &&
            held.ConflictVersions?.Incoming?.Target == Side.B && held.ConflictVersions.B.ObjectId == "unrelated",
            "rule14 notice binds incoming action and actual occupant");
        Require(NoticeOptions.Options(held).Select(option => option.Choice).SequenceEqual(
            new[] { "rename-incoming", "replace-existing", "dismiss" }), "rule14 exposes exact entity choices");
        var kinds = Decide(Row(null, F("same", "new-file", "bytes"), D("SAME", "folder")));
        Require(kinds.Actions.All(action => action.Kind == ActionKind.Hold) && kinds.Notices.Single().ConflictVersions?.B.Kind == ItemKind.Folder,
            "extensionless file/folder case-only clash preserves both identities and creates nothing");
        Require(ConflictText.Merge("one\ntwo\nthree\n", "ONE\ntwo\nthree\n", "one\ntwo\nTHREE\n") == "ONE\ntwo\nTHREE\n",
            "three-way merge combines disjoint edits against shared baseline");
        Require(ConflictText.Merge("base", "left", "right") is null, "overlap is displayed, never silently resolved");
        Require(ConflictText.Merge("base", "base", "right") == "right", "unchanged side follows edited side");
        Require(ConflictText.Decode([0, 1, 2]) is null && ConflictText.Decode([0xff]) is null, "binary and invalid UTF8 cannot merge");
        Console.WriteLine("Conflict choices: planner provenance, policy, codec, option vocabulary, merge checks passed.");
        return Task.CompletedTask;
    }
}
