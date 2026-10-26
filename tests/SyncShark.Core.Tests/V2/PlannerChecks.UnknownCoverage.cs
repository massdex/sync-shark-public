namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void UnknownCoverage()
    {
        var a = F("Parent/File", "a", "old");
        var b = F(a.Path, "b", "old");
        var basis = Base("linked", a, b);
        var coverage = Complete() with
        {
            Unknown = (_, path) => path.StartsWith("Parent/", StringComparison.OrdinalIgnoreCase),
            ExactCreationObservedA = new HashSet<string> { a.Path },
            ExactCreationObservedB = new HashSet<string> { b.Path }
        };
        Plan Plan(IReadOnlyList<Baseline> bases, IReadOnlyList<ItemState> aa,
            IReadOnlyList<ItemState> bb, Completeness c) =>
            Planner.Decide(PairId, Gen, Planner.Join(bases, aa, bb, Set(), Set(), c, false), Opt());
        foreach (var present in new[] { true, false })
            Require(Plan([basis], present ? [a] : [], [], coverage).Actions.All(action =>
                action.Kind is not (ActionKind.Delete or ActionKind.Forget)),
                "unknown absence never authorizes Delete or Forget");
        Require(Plan([], [a], [], coverage).Actions.All(action => action.Kind != ActionKind.CopyNew),
            "unknown destination blocks creation even with cached exact evidence");
        Require(Plan([basis], [a with { Hash = "edited", Size = 2 }], [b], coverage).Actions.Any(action =>
            action.Kind == ActionKind.Update && action.Target == Side.B),
            "positive edit evidence still uploads within unknown coverage");
        Require(Plan([], [a], [], coverage with { Unknown = (_, _) => false }).Actions.Any(action =>
            action.Kind == ActionKind.CopyNew), "fresh exact path retains creation behaviour");
    }
}
