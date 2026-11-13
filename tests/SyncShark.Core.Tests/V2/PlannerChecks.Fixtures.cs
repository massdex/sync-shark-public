namespace Massdex.SyncShark.V2;

/// <summary>Fixtures and exact-match assertions shared by the Planner checks.</summary>
internal static partial class PlannerChecks
{
    // ---- fixtures ------------------------------------------------------------

    private static ItemState F(string path, string id, string? hash, long size = 1, DateTimeOffset? mtime = null,
        ItemFlags flags = ItemFlags.None, string? versionId = null) =>
        new(path, ItemKind.File, id, versionId, hash, size, mtime ?? T0, flags);

    private static ItemState D(string path, string id, DateTimeOffset? mtime = null) =>
        new(path, ItemKind.Folder, id, null, null, 0, mtime ?? T0, ItemFlags.None);

    private static Baseline Base(string key, ItemState a, ItemState b) => new(PairId, key, a, b, 1);

    // C19 / planner.md: ordinary no-baseline creation fixtures model an observed pair.
    // Unknown coverage must be explicit; baseline fixtures keep their historic unknown default
    // (notably Rule24b's unknown descendant). Production Join supplies the actual coverage.
    private static PlanRow Row(Baseline? b, ItemState? a, ItemState? bSide, Evidence? ev = null) =>
        new(b, a, bSide, ev ?? new Evidence(false, false, b is null, b is null));

    private static Evidence Ev(bool A = false, bool B = false, DateTimeOffset? atA = null, DateTimeOffset? atB = null) =>
        new(A, B, A, B, atA, atB);

    private static PlanOptions Opt(bool firstSync = false, TimeSpan? tie = null, IReadOnlyDictionary<string, Side?>? winners = null,
        IReadOnlyList<OneSidedChoice>? oneSided = null) =>
        new(tie ?? TimeSpan.FromSeconds(2), firstSync, Label, V2Settings.Default.MaxLabelAttempts, winners, oneSided)
        { ObservedAt = T0.AddDays(1), ProviderA = SyncProviders.Local, ProviderB = SyncProviders.Dropbox };

    /// <summary>The exact key of a new identity whose source item is on <paramref name="side"/>.</summary>
    private static string NK(Side side, string objectId) => PlanIds.ObjectKey(PairId, side, objectId);

    /// <summary>The exact key of an identity split off baseline <paramref name="key"/>.</summary>
    private static string SK(string key, Side side) => PlanIds.Split(key, side);

    private static IReadOnlyDictionary<string, DateTimeOffset?> Set(params string[] items) =>
        items.ToDictionary(item => item, _ => (DateTimeOffset?)null, StringComparer.OrdinalIgnoreCase);

    private static Completeness Complete() => new(true, true, new HashSet<string>(), new HashSet<string>());

    private static Side Opposite(Side side) => side == Side.A ? Side.B : Side.A;

    private static Plan Decide(PlanRow row) => Planner.Decide(PairId, Gen, new[] { row }, Opt());

    private static Plan Decide(bool firstSync, params PlanRow[] rows) => Planner.Decide(PairId, Gen, rows, Opt(firstSync));

    // ---- assertions ----------------------------------------------------------

    /// <summary>One expected action; <see cref="Key"/> is compared exactly (null means the action carries no key).</summary>
    private readonly record struct Exp(ActionKind Kind, Side Target, string Path, string? FromPath, bool Rf, int Rule,
        string? SourcePath = null, string? Key = null);
    private readonly record struct NExp(int Rule, NoticeKind Kind, string Path);

    private static void AssertActions(Plan plan, params Exp[] expected)
    {
        var actual = plan.Actions
            .OrderBy(a => a.Target).ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Path, StringComparer.Ordinal).ThenBy(a => (int)a.Kind).ToList();
        var want = expected
            .OrderBy(e => e.Target).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => (int)e.Kind).ToList();
        Require(actual.Count == want.Count, $"action count {actual.Count} == {want.Count}: " +
            string.Join(", ", actual.Select(a => $"{a.Kind} {a.Target} '{a.Path}' rule={a.Rule}")));
        for (var i = 0; i < want.Count; i++)
            AssertOne(actual[i], want[i], i);
    }

    private static void AssertOne(SyncAction a, Exp e, int i)
    {
        Require(a.Kind == e.Kind && a.Target == e.Target && a.Path == e.Path && a.FromPath == e.FromPath &&
            a.RecoveryFirst == e.Rf && a.Rule == e.Rule,
            $"action {i}: {a.Kind} {a.Target} '{a.Path}' from '{a.FromPath}' rf={a.RecoveryFirst} rule={a.Rule} " +
            $"== {e.Kind} {e.Target} '{e.Path}' from '{e.FromPath}' rf={e.Rf} rule={e.Rule}");
        if (e.SourcePath is not null)
            Require(a.Source?.Path == e.SourcePath, $"action {i} source '{a.Source?.Path}' == '{e.SourcePath}'");
        Require(a.ObjectKey == e.Key, $"action {i} key '{a.ObjectKey}' == '{e.Key}'");
        if (a.RecoveryFirst)
            Require(a.ExpectedTarget is not null, $"action {i} is recovery-first without a victim");
    }

    private static void AssertNotices(Plan plan, params NExp[] expected)
    {
        var actual = plan.Notices
            .OrderBy(n => n.Kind).ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Rule).ToList();
        var want = expected
            .OrderBy(e => e.Kind).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Rule).ToList();
        Require(actual.Count == want.Count, $"notice count {actual.Count} == {want.Count}");
        for (var i = 0; i < want.Count; i++)
            Require(actual[i].Kind == want[i].Kind && actual[i].Path == want[i].Path && actual[i].Rule == want[i].Rule,
                $"notice {i}: {actual[i].Kind} '{actual[i].Path}' rule={actual[i].Rule} == {want[i].Kind} '{want[i].Path}' rule={want[i].Rule}");
    }

    private static void AssertEmpty(Plan plan)
    {
        AssertNoActions(plan);
        AssertEmptyNotices(plan);
    }

    private static void AssertNoActions(Plan plan) => Require(plan.Actions.Count == 0, "no actions, got " + plan.Actions.Count);

    private static void AssertEmptyNotices(Plan plan) => Require(plan.Notices.Count == 0, "no notices, got " + plan.Notices.Count);

    private static void ExpectThrows<TException>(Action operation) where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException)
        {
            Console.WriteLine("PASS rejects via " + typeof(TException).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
