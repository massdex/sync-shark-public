using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Product-SQLite before/after issue-category and raw admission costs on synthetic R03/R14 issue counts.</summary>
internal static class IssueCategoryCostChecks
{
    // docs/known-facts.md R03 supplies 461 actual open issues; R14 supplies these synthetic stress counts.
    // The population below is synthetic, not a claim about the causes or density in the real pair.
    private static readonly int[] IssueCounts = [0, 461, 10_000, 100_000, 1_125_932, 1_526_314];
    private const string Pair = "cost";
    // engine-settings.json's adopted StoragePageSize, matching the existing StorageCostChecks operand.
    private const int PageSize = 100;
    // Exact pre-B38 CountIssues at 8c57c9272; the other three baselines differ only in the kind operand.
    private const string BaselineCounts = "SELECT kind, count(*) FROM issue WHERE pair_id = " +
        "(SELECT id FROM pair WHERE pair_id = $p) AND dismissed = 0 " +
        "AND ($ignored IS NULL OR ignored=$ignored) GROUP BY kind;";
    private const string SeedSql = """
        WITH RECURSIVE n(i) AS
         (SELECT 1 WHERE $count>0 UNION ALL SELECT i+1 FROM n WHERE i<$count)
        INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks)
        SELECT (SELECT id FROM pair WHERE pair_id=$p),$kind,
         CASE WHEN $mixed=1 AND i%2=0 THEN $waiting ELSE $cause END,
         'Parent/Item/' || printf('%010d',i),$subject,1 FROM n;
        """;
    private const string SetAdmissionSql = """
        UPDATE issue SET kind=$kind,cause=$cause,ignored=$ignored,dismissed=$dismissed
        WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND path=$path;
        """;

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "issue-category-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Console.WriteLine("Issue costs: product e_sqlite3; no ANALYZE, no added index; synthetic issue rows only.");
            Console.WriteLine("Admission comparison: strictly <= today; absent case O(I) as today. VM evidence follows.");
            foreach (var count in IssueCounts)
                foreach (var mixed in new[] { false, true })
                    MeasurePopulation(Path.Combine(folder, count + "-" + mixed + ".db"), count, mixed);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        Check.Require(!Directory.Exists(folder), "issue category cost files are removed after every population");
    }

    private static void MeasurePopulation(string path, int count, bool mixed)
    {
        using var probe = new CostProbe(path);
        probe.CreateSchema();
        probe.Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, Pair), (StateSchema.ParamDef, "{}"));
        probe.Execute(SeedSql, ("$count", count), ("$mixed", mixed), (StateSchema.ParamPair, Pair),
            (StateSchema.ParamKind, (int)IssueKind.Failure), ("$waiting", nameof(TextKey.FailLocalInUse)),
            (StateSchema.ParamCause, nameof(TextKey.FailChanged)),
            (StateSchema.ParamSubject, JsonSerializer.Serialize(new IssueSubject([], null))));
        Console.WriteLine($"population I={count} mixed={mixed} (not a synced-entry count)");
        PublicQueries(probe);
        Admission(probe, "absent");
        if (count == 0) return;
        foreach (var position in new[] { 1, count }.Distinct())
        {
            SetAdmission(probe, position, IssueKind.DriveChange, false, false);
            Admission(probe, "present-" + position);
            SetAdmission(probe, position, IssueKind.DriveChange, true, false);
            Admission(probe, "ignored-" + position);
            SetAdmission(probe, position, IssueKind.DriveChange, true, true);
            Admission(probe, "dismissed-" + position);
            SetAdmission(probe, position, IssueKind.Failure, false, false);
        }
        Console.WriteLine("retained-db-bytes=" + new FileInfo(path).Length +
            " (synthetic issue-only store; excludes synced entries and is not write-traffic evidence)");
    }

    private static void PublicQueries(CostProbe probe)
    {
        foreach (var kinds in new string?[] { null, JsonSerializer.Serialize(new[] { (int)IssueKind.Failure }),
            JsonSerializer.Serialize(new[] { (int)IssueKind.Info }) })
        {
            (string Name, object? Value)[] arguments =
            [
                (StateSchema.ParamPair, Pair), (StateSchema.ParamAccount, null),
                (StateSchema.ParamSearch, null), (StateSchema.ParamKinds, kinds),
                (StateSchema.ParamIgnored, false), (StateSchema.ParamLimit, PageSize),
                (StateSchema.ParamOffset, 0), (StateSchema.ParamAfter, 0L),
                .. IssueTable.PublicCategoryParameters
            ];
            foreach (var (name, sql) in new[]
            {
                (nameof(StateSchema.SelectIssuePage), StateSchema.SelectIssuePage),
                (nameof(StateSchema.CountIssuePage), StateSchema.CountIssuePage),
                (nameof(StateSchema.SelectIssuePageAfter), StateSchema.SelectIssuePageAfter),
                (nameof(StateSchema.CountIssues), StateSchema.CountIssues)
            })
            {
                var baseline = name == nameof(StateSchema.CountIssues) ? BaselineCounts :
                    sql.Replace("(" + StateSchema.PublicIssueKindExpression + ")", "i.kind", StringComparison.Ordinal);
                var label = name + " kinds=" + (kinds ?? "all");
                var scalar = name == nameof(StateSchema.CountIssuePage);
                Measure(probe, label + " baseline-8c57c9272", baseline, scalar, arguments);
                Measure(probe, label + " candidate", sql, scalar, arguments);
            }
        }
    }

    private static void Admission(CostProbe probe, string shape)
    {
        (string Name, object? Value)[] rawArguments =
            [(StateSchema.ParamPair, Pair), (StateSchema.ParamKind, (int)IssueKind.DriveChange)];
        var plan = probe.ExplainPlan(StateSchema.HasOpenIssue, rawArguments);
        foreach (var detail in plan) Console.WriteLine("HasOpenIssue plan " + shape + ": " + detail);
        Check.Require(plan.Any(detail => detail.Contains("SEARCH issue USING", StringComparison.Ordinal) &&
            detail.Contains("INDEX", StringComparison.Ordinal)) &&
            !plan.Any(detail => detail.StartsWith("SCAN issue", StringComparison.Ordinal)),
            "raw-kind admission uses an existing pair-prefix index without statistics");
        (string Name, object? Value)[] countArguments =
        [
            (StateSchema.ParamPair, Pair), (StateSchema.ParamIgnored, null),
            .. IssueTable.PublicCategoryParameters
        ];
        var before = Measure(probe, "admission " + shape + " baseline-8c57c9272",
            BaselineCounts, false, countArguments);
        var today = Measure(probe, "admission " + shape + " projected-counts",
            StateSchema.CountIssues, false, countArguments);
        var candidate = Measure(probe, "admission " + shape + " raw-exists",
            StateSchema.HasOpenIssue, true, rawArguments);
        Check.Require(candidate.VmSteps <= before.VmSteps && candidate.VmSteps <= today.VmSteps,
            "raw-kind admission never exceeds either full-count statement's VM work for " + shape);
    }

    private static void SetAdmission(CostProbe probe, int position, IssueKind kind, bool ignored, bool dismissed) =>
        probe.Execute(SetAdmissionSql, (StateSchema.ParamPair, Pair), (StateSchema.ParamKind, (int)kind),
            (StateSchema.ParamCause, kind == IssueKind.DriveChange ? nameof(TextKey.DriveRestored) : nameof(TextKey.FailChanged)),
            (StateSchema.ParamIgnored, ignored), ("$dismissed", dismissed),
            (StateSchema.ParamPath, "Parent/Item/" + position.ToString("D10", CultureInfo.InvariantCulture)));

    private static StatementCost Measure(CostProbe probe, string label, string sql, bool scalar,
        (string Name, object? Value)[] arguments)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var cost = scalar ? probe.ExecuteFirst(sql, arguments) : probe.Execute(sql, arguments);
        var elapsed = Stopwatch.GetElapsedTime(started);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check.Require(cost.ChangedRows == 0, label + " is read-only");
        Console.WriteLine(FormattableString.Invariant(
            $"{label}: vm={cost.VmSteps} ms={elapsed.TotalMilliseconds:F4} managed-bytes={allocated} pathkey={cost.PathKeyCalls} pair-sources={cost.PairSourceOccurrences} json-sources={cost.JsonSourceOccurrences} probe-prepares={cost.ProbePrepares}"));
        return cost;
    }
}
