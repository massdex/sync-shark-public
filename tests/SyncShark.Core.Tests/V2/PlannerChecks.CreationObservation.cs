namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void CreationObservationNeverDeletes()
    {
        var a = D("Documents/Repeat/Repeat", "a");
        var b = D(a.Path, "b");
        var completeness = new Completeness(true, false, new HashSet<string>(), new HashSet<string> { "Unrelated" })
        { ExactCreationObservedB = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Path } };
        var fresh = Planner.Join([], [a], [], Set(), Set(), completeness, false);
        Require(fresh.Single().Ev is { KnownB: false, CreationObservedB: true },
            "exact creation evidence does not promote whole-side or baseline coverage");
        Require(Planner.Decide(PairId, Gen, fresh, Opt()).Actions.Single().Kind == ActionKind.MkDir,
            "an exact absent destination permits no-baseline folder creation");
        var existing = Planner.Join([Base("existing", a, b)], [a], [], Set(), Set(), completeness, false);
        Require(Planner.Decide(PairId, Gen, existing, Opt()).Actions.All(action => action.Kind is not (ActionKind.Delete or ActionKind.Forget)),
            "the same exact observation cannot authorize baseline deletion or Forget");
        foreach (var failed in new[] { "", "Documents/Repeat", a.Path })
        {
            var unknown = Planner.Join([], [a], [], Set(), Set(), completeness with
            { IncompleteSubtreesB = new HashSet<string> { failed } }, false);
            var plan = Planner.Decide(PairId, Gen, unknown, Opt());
            Require(!unknown.Single().Ev.CreationObservedB && plan.Actions.Count == 0 &&
                plan.Notices.Single().ReasonKey == PlanNotices.DestinationUnobserved.Key,
                "root, ancestor and exact failures remain unknown despite an earlier exact observation");
            Require(NoticeChoices.IsAdmissible(plan.Notices.Single(), NoticeChoices.RetryNow) &&
                NoticeChoices.IsAdmissible(plan.Notices.Single(), NoticeChoices.Ignore),
                "destination observation debt uses the shared Retry/Ignore notice surface");
        }
    }
}
