using System.Text.Json;
using Massdex.SyncShark;

internal static class UpdateDiscoveryChecks
{
    internal static void Run(string root, Action<string, string> createJunction)
    {
        Directory.CreateDirectory(root);
        const string current = "0.3.18+fixture-source";
        void Package(string directory, string version, string previous = current, string product = "Massdex.SyncShark", string? header = null)
        {
            var folder = Path.Combine(root, directory, "scripts"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Install-SyncShark.ps1"), header ??
                "# SyncSharkUpdateContract: " + JsonSerializer.Serialize(new { product, previousVersion = previous, version }) +
                "\nthrow 'Discovery must never execute this installer'\n");
        }
        Package("older", "0.3.17"); Package("same", "0.3.18"); Package("newer", "0.3.19");
        Package("newest", "0.3.20"); Package("wrong-source", "0.3.21", "0.3.18+other-source");
        Package("other-product", "0.3.99", product:"Other.Product"); Package("malformed", "0.3.22", header:"# SyncSharkUpdateContract: {bad}");
        var found = SyncUpdateDiscovery.FindCandidates(root, current);
        Require(found.Select(item => item.Version).SequenceEqual(new[] { "0.3.20", "0.3.19" }), "Only newer exact-predecessor product packages are discovered in version order");
        Require(found.All(item => File.Exists(item.InstallerPath)), "Discovered identities refer to existing scripts without executing their bodies");
        Package("oversized", "0.3.30", header:"# SyncSharkUpdateContract: " + new string('x', 20_000));
        Package("wrong-shape", "0.3.31", header:"# SyncSharkUpdateContract: []");
        Package("too-deep/nested", "0.3.32");
        Require(SyncUpdateDiscovery.FindCandidates(root, current).Count == 2, "Malformed, oversized-header and deeper-than-supported inputs are skipped");
        Require(SyncUpdateDiscovery.FindCandidates(root, "unknown").Count == 0, "Unknown installed identity never yields an update offer");
        try { SyncUpdateDiscovery.FindCandidates(Path.Combine(root, "missing"), current); throw new Exception("Missing root accepted"); }
        catch (IOException) { Require(true, "Unavailable delivery folder gives a recoverable error"); }
        if (OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(root, "alias");
            try
            {
                createJunction(alias, Path.Combine(root, "newer"));
                Require(SyncUpdateDiscovery.FindCandidates(root, current).Count == 2, "Reparse aliases are not traversed or offered as duplicate updates");
                try { SyncUpdateDiscovery.FindCandidates(alias, current); throw new Exception("Reparse root accepted"); }
                catch (IOException) { Require(true, "A reparse delivery root is rejected"); }
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("UNVERIFIED reparse creation is unavailable to this test process"); }
        }
    }
    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
}
