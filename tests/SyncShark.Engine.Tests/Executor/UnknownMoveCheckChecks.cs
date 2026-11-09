using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for UnknownMoveCheck (`--engine-checks resolutions`): one row per DESIGN-B70-TRANSIENT-10
/// section 11 observation.</summary>
public static class UnknownMoveCheckChecks
{
    private const string Native = "/Root/";

    public static void Run()
    {
        var move = new IssueSubject.UnknownMove("id:a", "Parent/Child", "Other/Child", null, false);
        var caseOnly = new IssueSubject.UnknownMove("id:a", "Parent/Child", "Parent/child", "Parent/child.case", false);
        var cases = new (string Name, IssueSubject.UnknownMove Move, IdentityLocation? Observed,
            UnknownMoveCheck.Outcome Expected)[]
        {
            ("at destination", move, Inside("Other/Child"), UnknownMoveCheck.Outcome.Adopt),
            ("at destination under a stale-cased ancestor", move, Inside("OTHER/Child"), UnknownMoveCheck.Outcome.Adopt),
            ("destination key with another leaf spelling", move, Inside("Other/CHILD"),
                UnknownMoveCheck.Outcome.ReplanFromObserved),
            ("at source", move, Inside("Parent/Child"), UnknownMoveCheck.Outcome.ReplanFromSource),
            ("elsewhere in the pair", move, Inside("Third/Child"), UnknownMoveCheck.Outcome.ReplanFromObserved),
            ("outside the pair", move, new IdentityLocation("ns", "/Elsewhere/Child", Entry("/Elsewhere/Child")),
                UnknownMoveCheck.Outcome.KeepUnresolved),
            ("proven absent", move, null, UnknownMoveCheck.Outcome.KeepUnknown),
            ("case-only, final spelling", caseOnly, Inside("Parent/child"), UnknownMoveCheck.Outcome.Adopt),
            ("case-only, source spelling", caseOnly, Inside("Parent/Child"), UnknownMoveCheck.Outcome.ReplanFromSource),
            ("case-only, at the intermediate", caseOnly, Inside("Parent/child.case"),
                UnknownMoveCheck.Outcome.RemainingHop)
        };
        foreach (var test in cases)
            Check.Require(UnknownMoveCheck.Decide(test.Move, test.Observed) == test.Expected,
                "unknown move: " + test.Name);
    }

    private static IdentityLocation Inside(string path) => new("ns", Native + path, Entry(path));

    private static Entry Entry(string path) => new(path, false, 1, DateTimeOffset.UnixEpoch, "id:a", "rev", "hash", null);
}
