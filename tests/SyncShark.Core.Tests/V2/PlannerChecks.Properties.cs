using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>Seeded property checks for the Planner and their row generator (planner.md section 8).</summary>
internal static partial class PlannerChecks
{
    private const int Seed = 20261015;
    private const int PropertyRows = 500;
    private const int Variants = 25;

    private static readonly TimeSpan Tie = TimeSpan.FromSeconds(2);

    private static void Property_NoDeleteWithoutEvidence(bool firstSync)
    {
        var owner = new Dictionary<string, (PlanRow Row, Side Side)>(StringComparer.Ordinal);
        var rows = GenerateRows(new Random(Seed), PropertyRows, owner);
        var plan = Planner.Decide(PairId, Gen, rows, Opt(firstSync: firstSync));
        foreach (var delete in plan.Actions.Where(a => a.Kind == ActionKind.Delete))
        {
            Require(delete.ExpectedTarget is not null, "delete without its victim");
            var victim = delete.ExpectedTarget!;
            Require(owner.ContainsKey(victim.ObjectId), "delete victim not from a generated row");
            var (row, side) = owner[victim.ObjectId];
            var other = Opposite(side);
            var otherCur = Cur(row, other);
            var otherBase = other == Side.A ? row.Base?.A : row.Base?.B;
            var revisionHash = delete.Rule == 6 ? otherBase?.Hash :
                other == Side.A ? row.Ev.DeletedHashA : row.Ev.DeletedHashB;
            // Decisions 69/78: skipping Recovery requires exact known revision equality, not merely a victim hash.
            Require(delete.RecoveryFirst ||
                (delete.Rule is 6 or 19) && victim.Kind == ItemKind.File && victim.Hash is not null &&
                revisionHash is not null && string.Equals(victim.Hash, revisionHash, StringComparison.Ordinal),
                "delete skips Recovery without an exact revision hash");
            var deleted = other == Side.A ? row.Ev.DeletedA || row.Ev.KnownA : row.Ev.DeletedB || row.Ev.KnownB;
            var replaced = otherCur is not null && otherBase is not null && otherCur.Kind != otherBase.Kind;
            // Rules 4/12: the old copy of a moved item; the move (the Delete's Source) is the evidence.
            var moved = delete.Rule is 4 or 12 && otherCur is not null && otherBase is not null &&
                delete.Source == otherCur && !PairPath.Same(otherCur.Path, otherBase.Path);
            // Rule 4 (first sync only): an occupant replaced by a version newer by more than the tie window.
            var newerWins = firstSync && delete.Rule == 4 && delete.Source is not null &&
                delete.Source.Mtime - victim.Mtime > Tie;
            Require(deleted || replaced || moved || newerWins || delete.Rule is 26 or 32,
                "delete without evidence: rule " + delete.Rule + " at " + delete.Path);
        }
    }

    /// <summary>Hold only for a first-sync or no-baseline tie (rule 5, decision 81), or a kind clash (rule 29), each with its UserDecision notice.</summary>
    private static void Property_HoldOnlyForDecisionRules()
    {
        foreach (var firstSync in new[] { false, true })
        {
            var rows = GenerateRows(new Random(Seed), PropertyRows, new Dictionary<string, (PlanRow, Side)>(StringComparer.Ordinal));
            var options = Opt(firstSync: firstSync) with { ObservedAt = T0.AddDays(1) };
            Require(rows.All(row =>
                (row.A is null || ModificationTime.IsPlausible(row.A.Mtime, options.ObservedAt)) &&
                (row.B is null || ModificationTime.IsPlausible(row.B.Mtime, options.ObservedAt))),
                "generated timestamps are plausible against the pinned observation time");
            var plan = Planner.Decide(PairId, Gen, rows, options);
            var holds = plan.Actions.Where(a => a.Kind == ActionKind.Hold).ToList();
            Require(holds.Any(h => h.Rule == 29), "generator reaches rule 29");
            Require(holds.Any(h => h.Rule == 5), $"generator reaches rule 5 (first sync {firstSync})");
            foreach (var hold in holds)
            {
                // Preserve the original first-sync allowance: occupied moves can hold across two
                // rows (Variant 22), not just a same-row clash. Decision 81 adds only the bounded
                // ordinary no-baseline case below; its plausible generated timestamps must tie.
                var ordinaryNoBaselineTie = !firstSync && hold.Rule == 5 && rows.Any(row =>
                    row.Base is null && row.A is { Kind: ItemKind.File } a &&
                    row.B is { Kind: ItemKind.File } b && PairPath.Same(a.Path, hold.Path) &&
                    PairPath.Same(b.Path, hold.Path) && Content.SameContent(a, b) == false &&
                    (a.Mtime - b.Mtime).Duration() <= Tie);
                Require(hold.Rule == 29 || (hold.Rule == 5 && firstSync) || ordinaryNoBaselineTie,
                    $"Hold by rule {hold.Rule} (first sync {firstSync})");
                Require(plan.Notices.Any(n => n.Kind == NoticeKind.UserDecision && n.Rule == hold.Rule &&
                    PairPath.Same(n.Path, hold.Path)), "Hold without its UserDecision notice at " + hold.Path);
                Require(holds.Count(h => PairPath.Same(h.Path, hold.Path)) == 1, "two Holds at " + hold.Path);
                Require(!plan.Actions.Any(a => a.Kind != ActionKind.Hold && PairPath.Same(a.Path, hold.Path)),
                    "an action still runs at held " + hold.Path);
            }
        }
    }

    private static void Property_Determinism()
    {
        var rng = new Random(Seed);
        var rows = GenerateRows(rng, PropertyRows, new Dictionary<string, (PlanRow, Side)>(StringComparer.Ordinal));
        var first = Planner.Decide(PairId, Gen, rows, Opt());
        var shuffled = rows.ToList();
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        Require(SamePlan(first, Planner.Decide(PairId, Gen, shuffled, Opt())), "Property_Determinism");
    }

    // ---- generator for the property checks -----------------------------------

    /// <summary>Builds independent rows (one folder each) over side states, flags, folders, evidence and collisions.</summary>
    private static List<PlanRow> GenerateRows(Random rng, int count, Dictionary<string, (PlanRow, Side)> owner)
    {
        var rows = new List<PlanRow>(count);
        for (var i = 0; i < count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            var variant = rng.Next(Variants);
            var skew = TimeSpan.FromSeconds(rng.Next(3) switch { 0 => 10, 1 => -10, _ => 1 });
            var before = rows.Count;
            rows.AddRange(Variant(variant, "r" + n + "/", "k" + n, "A" + n, "B" + n, skew));
            for (var r = before; r < rows.Count; r++)
                foreach (var side in new[] { Side.A, Side.B })
                    if (Cur(rows[r], side) is { } cur)
                        owner[cur.ObjectId] = (rows[r], side);
        }
        return rows;
    }

    private static IEnumerable<PlanRow> Variant(int variant, string dir, string k, string idA, string idB, TimeSpan skew)
    {
        var x = dir + "x";
        var y = dir + "y";
        var basis = Base(k, F(x, idA, "h1"), F(x, idB, "h1"));
        switch (variant)
        {
            case 0: return new[] { Row(basis, F(x, idA, "h2"), F(x, idB, "h1")) };
            case 1: return new[] { Row(basis, F(x, idA, "h1"), F(x, idB, "h2")) };
            case 2: return new[] { Row(basis, F(x, idA, "h2"), F(x, idB, "h2")) };
            case 3: return new[] { Row(basis, F(x, idA, "h2"), F(x, idB, "h3")) };
            case 4: return new[] { Row(basis, F(y, idA, "h1"), F(x, idB, "h1")) };
            case 5: return new[] { Row(basis, F(x, idA, "h1"), F(y, idB, "h1")) };
            case 6: return new[] { Row(basis, null, F(x, idB, "h1"), Ev(A: true)) };
            case 7: return new[] { Row(basis, null, F(x, idB, "h2"), Ev(A: true)) };
            case 8: return new[] { Row(basis, null, F(y, idB, "h1"), Ev(A: true)) };
            case 9: return new[] { Row(basis, null, null, Ev(A: true, B: true)) };
            case 10: return new[] { Row(null, F(x, idA, "h1"), null) };
            case 11: return new[] { Row(null, F(x, idA, "h1"), F(x, idB, "h2")) };
            case 12: return new[] { Row(basis, F(y, idA, "h2"), F(x, idB, "h1")) };
            case 13: return new[] { Row(null, F(x, idA, "h1"), F(x, idB, "h1")) };
            case 14: return new[] { Row(basis, F(y, idA, "h1"), F(y, idB, "h1")) };
            case 15: return new[] { Row(basis, F(x, idA, "h2", flags: ItemFlags.Ignored), F(x, idB, "h1")) };
            case 16: return new[] { Row(basis, null, F(x, idB, "h1")) };
            case 17: return new[] { Row(Base(k, D(dir + "F", idA), D(dir + "F", idB)), D(dir + "G", idA), D(dir + "F", idB)) };
            case 18: return FolderDelete(dir, k, idA, idB, "h1");
            case 19: return FolderDelete(dir, k, idA, idB, "h2");
            case 20: return OccupiedMove(dir, k, idA, idB, deletedOccupant: true, T0);
            case 21: return new[] { Row(null, F(x, idA, "h1", mtime: T0), null, Ev(B: true, atB: T0 + TimeSpan.FromHours(1))) };
            case 22: return OccupiedMove(dir, k, idA, idB, deletedOccupant: false, T0 + skew);
            case 23:
                return new[]
                {
                    Row(Base(k, F(dir + "p", idA, "h1"), F(dir + "p", idB, "h1")), F(dir + "q", idA, "h1"), F(dir + "p", idB, "h1")),
                    Row(null, null, F(dir + "q", idB + "o", "h1")),
                };
            default: return new[] { Row(Base(k, F(x, idA, "h1"), F(x, idB, "h1")), D(x, idA), F(x, idB, "h2")) };
        }
    }

    private static PlanRow[] FolderDelete(string dir, string k, string idA, string idB, string childHashB) => new[]
    {
        Row(Base(k, D(dir + "F", idA), D(dir + "F", idB)), null, D(dir + "F", idB), Ev(A: true)),
        Row(Base(k + "c", F(dir + "F/c", idA + "c", "h1"), F(dir + "F/c", idB + "c", "h1")),
            null, F(dir + "F/c", idB + "c", childHashB), Ev(A: true)),
    };

    /// <summary>A moves p→q while B holds another item at q (deleted on A, or with no evidence on A).</summary>
    private static PlanRow[] OccupiedMove(string dir, string k, string idA, string idB, bool deletedOccupant,
        DateTimeOffset movedMtime) => new[]
    {
        Row(Base(k, F(dir + "p", idA, "h1"), F(dir + "p", idB, "h1")), F(dir + "q", idA, "h1", mtime: movedMtime),
            F(dir + "p", idB, "h1")),
        Row(Base(k + "o", F(dir + "q", idA + "o", "h7"), F(dir + "q", idB + "o", "h7")),
            null, F(dir + "q", idB + "o", "h7"), deletedOccupant ? Ev(A: true) : null),
    };

    private static ItemState? Cur(PlanRow row, Side side) => side == Side.A ? row.A : row.B;

    private static bool SamePlan(Plan x, Plan y)
    {
        if (x.Actions.Count != y.Actions.Count || x.Notices.Count != y.Notices.Count) return false;
        for (var i = 0; i < x.Actions.Count; i++)
            if (x.Actions[i] != y.Actions[i]) return false;
        for (var i = 0; i < x.Notices.Count; i++)
            if (x.Notices[i] != y.Notices[i]) return false;
        return true;
    }
}
