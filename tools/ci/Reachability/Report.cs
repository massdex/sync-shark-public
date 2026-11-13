using System.Diagnostics;
using System.Text.Json;

internal sealed class Report(Analysis graph)
{
    internal void Write(string output, Stopwatch clock)
    {
        var live = graph.Walk("LIVE");
        var gui = graph.Walk("GUI");
        var all = graph.Walk("ALL");
        var labels = graph.Types.Values.Where(t => t.Product).ToDictionary(t => t.Id,
            t => live.ContainsKey(t.Id) ? "LIVE" : gui.ContainsKey(t.Id) ? "GUI-ONLY" : all.ContainsKey(t.Id) ? "ROUTE-ONLY" : "DEAD");
        var tests = graph.Types.Values.Where(t => !t.Product).ToArray();
        var unknown = tests.Where(t => t.Unknown || !t.Edges.Any(e => graph.Types[e.Target].Product)).Select(t => t.Id).ToHashSet();
        // Wrappers inherit uncertainty; kept consumers retain their shared fixtures.
        bool changed;
        do
        {
            changed = false;
            foreach (var test in tests)
                if (test.Edges.Any(e => unknown.Contains(e.Target))) changed |= unknown.Add(test.Id);
        } while (changed);
        var deadTests = tests.Where(t => !unknown.Contains(t.Id) && t.Edges.Where(e => graph.Types[e.Target].Product).All(e => labels[e.Target] == "DEAD")).Select(t => t.Id).ToHashSet();
        do
        {
            changed = false;
            foreach (var test in tests.Where(t => !deadTests.Contains(t.Id)))
            foreach (var edge in test.Edges) changed |= deadTests.Remove(edge.Target);
        } while (changed);
        foreach (var test in tests) labels[test.Id] = deadTests.Contains(test.Id) ? "TEST-OF-DEAD" : "UNKNOWN";
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
        var referrers = graph.Types.Values.SelectMany(t => t.Edges.Select(e => (e.Target, t.Id))).GroupBy(e => e.Target).ToDictionary(g => g.Key, g => string.Join(";", g.Select(e => e.Id).Distinct()));
        string Why(TypeNode node)
        {
            if (!all.ContainsKey(node.Id)) return referrers.GetValueOrDefault(node.Id, "no-referrers");
            var chain = new List<string>();
            string? id = node.Id;
            while (id is not null) { chain.Add(id); id = all[id].Parent; }
            chain.Reverse();
            return string.Join(" -> ", chain);
        }
        var rows = new List<Row>();
        foreach (var (path, nodes) in graph.Files.Where(f => File.Exists(Path.Combine(graph.Root, f.Key))))
        {
            var lines = File.ReadLines(Path.Combine(graph.Root, path)).Count();
            foreach (var node in nodes) rows.Add(new(path, node.Project, node.Id, labels[node.Id], string.Join(";", node.Roots), string.Join(";", node.Risks), Why(node), lines));
            var label = nodes.Select(n => labels[n.Id]).OrderBy(Rank).First();
            rows.Add(new(path, string.Join(";", nodes.Select(n => n.Project).Distinct()), "", label,
                string.Join(";", nodes.SelectMany(n => n.Roots).Distinct()), string.Join(";", nodes.SelectMany(n => n.Risks).Distinct()), "whole-file roll-up", lines));
        }
        // Only compiled product C# files can be candidates; retain assets and excluded source explicitly.
        foreach (var path in graph.Tracked.Where(p => !graph.Files.ContainsKey(p)))
            rows.Add(new(path, "", "", "UNKNOWN", "kept:asset-or-uncompiled", "component-edge", "not eligible", 0));
        foreach (var row in rows.Where(r => r.type.Length == 0 && r.label == "DEAD"))
            if (!row.path.EndsWith(".cs") || graph.Files[row.path].Any(n => !n.Product || n.Roots.Count != 0)) throw new InvalidOperationException("Ineligible DEAD file: " + row.path);
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        File.WriteAllLines(Path.Combine(output, "reachability.csv"), new[] { "path,projects,type,label,reasons,risk,why,lines" }.Concat(rows.OrderBy(r => r.path).ThenBy(r => r.type).Select(r => string.Join(",", new[] { r.path, r.projects, r.type, r.label, r.reasons, r.risk, r.why, r.lines.ToString() }.Select(Quote)))));
        var files = rows.Where(r => r.type == "").ToArray();
        var summary = new {
            valid = true, candidateOnly = true, semantics = "LIVE means conservatively retained; DEAD is not deletion authority.",
            counts = files.GroupBy(r => r.label).ToDictionary(g => g.Key, g => new { files = g.Count(), lines = g.Sum(r => (long)r.lines) }),
            types = labels.Values.GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count()),
            projects = rows.Where(r => r.type != "").GroupBy(r => r.projects).ToDictionary(g => g.Key, g => g.GroupBy(r => r.label).ToDictionary(x => x.Key, x => new { rows = x.Count(), lines = x.Sum(r => (long)r.lines) })),
            reasons = files.SelectMany(r => r.reasons.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(reason => (reason, r.lines))).GroupBy(r => r.reason).ToDictionary(g => g.Key, g => new { files = g.Count(), lines = g.Sum(x => (long)x.lines) }),
            routeLines = graph.Config.Cuts.Select(c => c.Route).Distinct().ToDictionary(route => route, route => {
                var roots = graph.Types.Values.SelectMany(t => t.Edges).Where(e => !graph.Allowed(e, "LIVE") && graph.CutsFor(e).Any(c => c.Route == route)).Select(e => e.Target);
                var reached = graph.Walk("LIVE", roots);
                return files.Where(f => f.label != "UNKNOWN" && graph.Files[f.path].Any(t => reached.ContainsKey(t.Id)) && !graph.Files[f.path].Any(t => live.ContainsKey(t.Id))).Sum(f => (long)f.lines); }),
            topDeadFiles = files.Where(r => r.label == "DEAD").OrderByDescending(r => r.lines).Take(20).Select(r => new { r.path, r.lines }),
            components = graph.Components, diagnostics = graph.Diagnostics, elapsedSeconds = clock.Elapsed.TotalSeconds,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(output, "summary.txt"), new[] { summary.semantics }.Concat(summary.counts.Select(c => $"{c.Key}: {c.Value.files} files, {c.Value.lines} lines")).Concat(summary.topDeadFiles.Select(f => $"{f.lines} {f.path}")));
    }
    static int Rank(string label) => label switch { "LIVE" => 0, "GUI-ONLY" => 1, "ROUTE-ONLY" => 2, "UNKNOWN" => 3, "TEST-OF-DEAD" => 4, _ => 5 };
    sealed record Row(string path, string projects, string type, string label, string reasons, string risk, string why, int lines);
}
