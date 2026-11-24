using System.Diagnostics;
using System.Text.Json;

internal sealed class Report(Analysis graph)
{
    Dictionary<string, (string? Parent, string Reason)> live = [], gui = [], all = [];
    Dictionary<string, string> labels = [], referrers = [];

    internal void Write(string output, Stopwatch clock)
    {
        live = graph.Walk("LIVE");
        gui = graph.Walk("GUI");
        all = graph.Walk("ALL");
        labels = graph.Types.Values.Where(t => t.Product).ToDictionary(t => t.Id,
            t => live.ContainsKey(t.Id) ? "LIVE" : gui.ContainsKey(t.Id) ? "GUI-ONLY" : all.ContainsKey(t.Id) ? "ROUTE-ONLY" : "DEAD");
        ClassifyTests();
        ValidateSentinels();
        referrers = graph.Types.Values.SelectMany(t => t.Edges.Select(e => (e.Target, t.Id))).GroupBy(e => e.Target).ToDictionary(g => g.Key, g => string.Join(";", g.Select(e => e.Id).Distinct()));
        var rows = SourceRows();
        AddIneligibleRows(rows);
        ValidateCandidates(rows);
        WriteCsv(output, rows);
        WriteSummary(output, rows, clock);
    }

    void ClassifyTests()
    {
        var tests = graph.Types.Values.Where(t => !t.Product).ToArray();
        foreach (var test in tests.Where(t => !t.Edges.Any(e => graph.Types[e.Target].Product))) test.UnknownReasons.Add("test:no-product-references");
        var unknown = tests.Where(t => t.UnknownReasons.Count != 0).Select(t => t.Id).ToHashSet();
        // Wrappers inherit uncertainty; kept consumers retain their shared fixtures.
        bool changed;
        do
        {
            changed = false;
            foreach (var test in tests)
                if (test.Edges.Any(e => e.Target != test.Id && unknown.Contains(e.Target)))
                {
                    test.UnknownReasons.Add("test:unknown-helper-dependency");
                    changed |= unknown.Add(test.Id);
                }
        } while (changed);
        foreach (var test in tests.Where(t => t.Edges.Any(e => graph.Types[e.Target].Product && labels[e.Target] != "DEAD")))
            test.UnknownReasons.Add("test:retained-product-reference");
        var deadTests = tests.Where(t => !unknown.Contains(t.Id) && t.Edges.Where(e => graph.Types[e.Target].Product).All(e => labels[e.Target] == "DEAD")).Select(t => t.Id).ToHashSet();
        do
        {
            changed = false;
            foreach (var test in tests.Where(t => !deadTests.Contains(t.Id)))
            foreach (var edge in test.Edges)
                if (deadTests.Remove(edge.Target))
                {
                    graph.Types[edge.Target].UnknownReasons.Add("test:retained-test-consumer");
                    changed = true;
                }
        } while (changed);
        foreach (var test in tests) labels[test.Id] = deadTests.Contains(test.Id) ? "TEST-OF-DEAD" : "UNKNOWN";
    }

    void ValidateSentinels()
    {
        foreach (var sentinel in graph.Config.Sentinels)
        {
            var nodes = graph.Types.Values.Where(t => t.Product && t.Symbol.ToDisplayString() == sentinel.Type).ToArray();
            var reached = sentinel.Profile == "LIVE" ? live : sentinel.Profile == "GUI" ? gui : all;
            if (nodes.Length == 0 || nodes.Any(t => !reached.ContainsKey(t.Id))) throw new InvalidOperationException($"Safety sentinel failed: {sentinel.Type} {sentinel.Profile}");
        }
        foreach (var check in graph.Config.EdgeChecks)
        {
            var edges = graph.Types.Values.SelectMany(t => t.Edges).Where(e => graph.CutsFor(e).Any(c => c.Route == check.Route) && graph.Types[e.Target].Symbol.ToDisplayString() == check.Target && !graph.Allowed(e, "LIVE")).ToArray();
            if (edges.Length == 0 || edges.Any(e => !graph.Allowed(e, "ALL")) || check.Route == "gui" && edges.Any(e => !graph.Allowed(e, "GUI")))
                throw new InvalidOperationException($"Route edge sentinel failed: {check.Route} {check.Target}");
        }
    }

    string Why(TypeNode node)
    {
        if (!all.ContainsKey(node.Id)) return referrers.GetValueOrDefault(node.Id, "no-referrers");
        var chain = new List<string>();
        string? id = node.Id;
        while (id is not null) { chain.Add(id); id = all[id].Parent; }
        chain.Reverse();
        return string.Join(" -> ", chain);
    }

    string Reasons(TypeNode node) => labels[node.Id] == "DEAD" ? "unreached:ALL" : string.Join(";", node.Roots.Concat(node.UnknownReasons).Concat(all.TryGetValue(node.Id, out var keep) ? new[] { keep.Reason } : []).Distinct());

    List<Row> SourceRows()
    {
        var rows = new List<Row>();
        var tracked = graph.Tracked.ToHashSet(StringComparer.Ordinal);
        foreach (var (path, nodes) in graph.Files.Where(f => tracked.Contains(f.Key)))
        {
            var lines = File.ReadLines(Path.Combine(graph.Root, path)).Count();
            foreach (var node in nodes) rows.Add(new(path, node.Project, node.Id, labels[node.Id], Reasons(node), string.Join(";", node.Risks), Why(node), lines));
            var label = nodes.Select(n => labels[n.Id]).OrderBy(Rank).First();
            rows.Add(new(path, string.Join(";", nodes.Select(n => n.Project).Distinct()), "", label,
                string.Join(";", nodes.SelectMany(n => Reasons(n).Split(';', StringSplitOptions.RemoveEmptyEntries)).Distinct()), string.Join(";", nodes.SelectMany(n => n.Risks).Distinct()), "whole-file roll-up", lines));
        }
        return rows;
    }

    void AddIneligibleRows(List<Row> rows)
    {
        // Only compiled product C# files can be candidates; retain assets and excluded source explicitly.
        var compiledPaths = graph.Compilations.Values.SelectMany(c => c.SyntaxTrees).Select(t => graph.Relative(t.FilePath)).ToHashSet();
        foreach (var path in graph.Tracked.Where(p => !graph.Files.ContainsKey(p)))
        {
            var reason = path.EndsWith(".cs") ? compiledPaths.Contains(path) ? "source:no-named-type" : "source:not-compiled" : "asset:" + Path.GetExtension(path).TrimStart('.');
            rows.Add(new(path, "", "", "UNKNOWN", reason, "component-edge", "not eligible", 0));
        }
    }

    void ValidateCandidates(List<Row> rows)
    {
        if (rows.Any(r => r.type.Length == 0 && r.label == "UNKNOWN" && r.reasons.Length == 0)) throw new InvalidOperationException("UNKNOWN file without an explanation.");
        foreach (var row in rows.Where(r => r.type.Length == 0 && r.label == "DEAD"))
            if (!row.path.EndsWith(".cs") || graph.Files[row.path].Any(n => !n.Product || n.Roots.Count != 0)) throw new InvalidOperationException("Ineligible DEAD file: " + row.path);
    }

    static void WriteCsv(string output, List<Row> rows)
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        File.WriteAllLines(Path.Combine(output, "reachability.csv"), new[] { "path,projects,type,label,reasons,risk,why,lines" }.Concat(rows.OrderBy(r => r.path).ThenBy(r => r.type).Select(r => string.Join(",", new[] { r.path, r.projects, r.type, r.label, r.reasons, r.risk, r.why, r.lines.ToString() }.Select(Quote)))));
    }

    object UnknownBreakdown(Row[] files)
    {
        var unknownFiles = files.Where(r => r.label == "UNKNOWN").Select(row => {
            var textLines = row.lines;
            if (!graph.Files.ContainsKey(row.path))
            {
                var bytes = File.ReadAllBytes(Path.Combine(graph.Root, row.path));
                textLines = bytes.Contains((byte)0) ? 0 : bytes.Count(b => b == '\n') + (bytes.Length != 0 && bytes[^1] != '\n' ? 1 : 0);
            }
            return new { row, textLines };
        }).ToArray();
        return unknownFiles.SelectMany(f => f.row.reasons.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(reason => (reason, file: f)))
            .GroupBy(x => x.reason).ToDictionary(g => g.Key, g => new {
                files = g.Count(), reportedLines = g.Sum(x => (long)x.file.row.lines), textLines = g.Sum(x => (long)x.file.textLines),
                topFiles = g.OrderByDescending(x => x.file.textLines).ThenBy(x => x.file.row.path).Take(10).Select(x => new { x.file.row.path, lines = x.file.textLines, x.file.row.why }) });
    }

    Dictionary<string, long> RouteLines(Row[] files) => graph.Config.Cuts.Select(c => c.Route).Distinct().ToDictionary(route => route, route => {
        var roots = graph.Types.Values.SelectMany(t => t.Edges).Where(e => !graph.Allowed(e, "LIVE") && graph.CutsFor(e).Any(c => c.Route == route)).Select(e => e.Target);
        var reached = graph.Walk("LIVE", roots);
        return files.Where(f => f.label != "UNKNOWN" && graph.Files[f.path].Any(t => reached.ContainsKey(t.Id)) && !graph.Files[f.path].Any(t => live.ContainsKey(t.Id))).Sum(f => (long)f.lines); });

    void WriteSummary(string output, List<Row> rows, Stopwatch clock)
    {
        var files = rows.Where(r => r.type == "").ToArray();
        var summary = new {
            valid = true, candidateOnly = true, semantics = "LIVE means conservatively retained; DEAD is not deletion authority.",
            counts = files.GroupBy(r => r.label).ToDictionary(g => g.Key, g => new { files = g.Count(), lines = g.Sum(r => (long)r.lines) }),
            types = rows.Where(r => r.type != "").DistinctBy(r => r.type).GroupBy(r => r.label).ToDictionary(g => g.Key, g => g.Count()),
            unknownExplanation = "UNKNOWN retains tests or ineligible assets, not compiler errors (which fail the run). Reasons overlap. Text lines for assets are measured only in this breakdown; class totals retain their prior zero-line convention for assets. Binary assets report zero text lines.",
            unknownBreakdown = UnknownBreakdown(files),
            projects = rows.Where(r => r.type != "").GroupBy(r => r.projects).ToDictionary(g => g.Key, g => g.GroupBy(r => r.label).ToDictionary(x => x.Key, x => new { rows = x.Count(), lines = x.Sum(r => (long)r.lines) })),
            reasons = files.SelectMany(r => r.reasons.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(reason => (reason, r.lines))).GroupBy(r => r.reason).ToDictionary(g => g.Key, g => new { files = g.Count(), lines = g.Sum(x => (long)x.lines) }),
            routeLines = RouteLines(files),
            topDeadFiles = files.Where(r => r.label == "DEAD").OrderByDescending(r => r.lines).Take(20).Select(r => new { r.path, r.lines }),
            deadCandidates = files.Where(r => r.label == "DEAD").OrderByDescending(r => r.lines).Select(r => new {
                r.path, r.lines, reason = "Unreached from every ALL root; no root-to-type chain exists.",
                types = graph.Files[r.path].Select(t => new { type = t.Id, referrers = Why(t) }) }),
            components = graph.Components, elapsedSeconds = clock.Elapsed.TotalSeconds,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(output, "summary.txt"), new[] { summary.semantics }.Concat(summary.counts.Select(c => $"{c.Key}: {c.Value.files} files, {c.Value.lines} lines")).Concat(summary.topDeadFiles.Select(f => $"{f.lines} {f.path}")));
    }

    static int Rank(string label) => label switch { "LIVE" => 0, "GUI-ONLY" => 1, "ROUTE-ONLY" => 2, "UNKNOWN" => 3, "TEST-OF-DEAD" => 4, _ => 5 };
    sealed record Row(string path, string projects, string type, string label, string reasons, string risk, string why, int lines);
}
