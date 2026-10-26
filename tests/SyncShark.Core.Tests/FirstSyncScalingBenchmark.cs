using System.Diagnostics;
using System.Text;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Opt-in scaling benchmark (--first-sync-scaling-benchmark <files-root> [<db-root>] [paths...]): the real CLI preparation
// (inventory save, matching-path recording, complete first-sync comparison) over a real local folder and
// the existing fake remote, at several synthetic sizes. Mix per size: 85% identical, 5% different
// content, 5% local only, 5% remote only. Prints per-phase seconds, per-path cost and the database size,
// then a linear fit and a 550k-path prediction. Put <db-root> on the disk that holds the real database.
// No live provider, credential or production state is used.
internal static class FirstSyncScalingBenchmark
{
    private static readonly string[] Phases =
        ["Saving folder inventories", "Updating folder index", "Recording matching paths and conflicts",
         "Complete first-sync comparison"];

    internal static async Task RunAsync(string root, string stateRoot, IReadOnlyList<int> sizes)
    {
        Directory.CreateDirectory(root);
        // The benchmark deletes folders under its root between runs, so it only runs in a root it owns.
        var owned = Path.Combine(root, ".first-sync-scaling-benchmark");
        if (!File.Exists(owned))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("Use an empty folder (or one this benchmark created) as the benchmark root.");
            File.WriteAllText(owned, "");
        }
        var results = new List<(int Paths, double[] Seconds, long Bytes)>();
        foreach (var size in sizes)
        {
            var result = await RunOneAsync(root, stateRoot, size);
            results.Add(result);
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.Combine(stateRoot, "state-" + size), true);
            Console.WriteLine($"size {size,8} paths: " + string.Join("  ", Phases.Select((name, i) =>
                $"{name}={result.Seconds[i]:F1}s ({result.Seconds[i] * 1e6 / size:F0}us/path)")) +
                $"  db={result.Bytes / 1048576.0:F1}MB ({(double)result.Bytes / size:F0}B/path)");
        }
        if (results.Count < 2) return;
        Console.WriteLine("Linear fit seconds = a + b*paths; prediction at 549,151 paths:");
        for (var i = 0; i < Phases.Length; i++)
        {
            var (a, b) = Fit(results.Select(item => ((double)item.Paths, item.Seconds[i])).ToArray());
            var first = results[0];
            var last = results[^1];
            var growth = (last.Seconds[i] / Math.Max(first.Seconds[i], 1e-3)) / ((double)last.Paths / first.Paths);
            Console.WriteLine($"  {Phases[i]}: b={b * 1e6:F0}us/path a={a:F1}s predicted={(a + b * 549_151) / 60:F1}min " +
                              $"(per-path cost ratio largest/smallest size {growth:F2}; ~1 means linear)");
        }
        var (da, db) = Fit(results.Select(item => ((double)item.Paths, (double)item.Bytes)).ToArray());
        Console.WriteLine($"  database: {db:F0}B/path predicted={(da + db * 549_151) / 1073741824.0:F2}GB");
    }

    private static (double A, double B) Fit((double X, double Y)[] points)
    {
        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var b = points.Sum(p => (p.X - mx) * (p.Y - my)) / points.Sum(p => (p.X - mx) * (p.X - mx));
        return (my - b * mx, b);
    }

    private static async Task<(int Paths, double[] Seconds, long Bytes)> RunOneAsync(string root, string stateRootBase, int size)
    {
        var localRoot = Path.Combine(root, "local-" + size);
        var marker = Path.Combine(localRoot, ".complete");
        var matching = size * 85 / 100;
        var different = size * 5 / 100;
        var localOnly = size * 5 / 100;
        var remoteOnly = size - matching - different - localOnly;
        static string Name(int index) => $"d{index / 500:D4}/f{index:D7}.txt";
        if (!File.Exists(marker))
        {
            if (Directory.Exists(localRoot)) Directory.Delete(localRoot, true);
            for (var i = 0; i < matching + different + localOnly; i++)
            {
                var path = Path.Combine(localRoot, Name(i));
                if (i % 500 == 0) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "content " + i);
            }
            await File.WriteAllTextAsync(marker, "");
        }
        File.Delete(marker);
        var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        for (var i = 0; i < matching + different; i++)
        {
            if (i % 500 == 0) remote.PutDirectory($"d{i / 500:D4}");
            remote.Put(Name(i), i < matching ? "content " + i : "remote content " + i);
        }
        for (var i = 0; i < remoteOnly; i++)
        {
            var index = matching + different + localOnly + i;
            if (index % 500 == 0 || i == 0) remote.PutDirectory($"d{index / 500:D4}");
            remote.Put(Name(index), "remote only " + index);
        }

        // Each run is a new installation; remove the previous run's control folder beside the local root.
        foreach (var control in Directory.GetDirectories(root, ".syncshark-control-*"))
            Directory.Delete(control, true);
        var stateRoot = Path.Combine(stateRootBase, "state-" + size);
        if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        Directory.CreateDirectory(stateRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "instance.lock"));
        var authority = instance.StateAuthority;
        var connections = new DropboxConnectionStore(authority);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:scaling", "scale-root", "scale-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await connections.SaveNewAsync(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(stateRoot, "authority.db"));
        using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), connections, new FirstSyncEffectChecks.FixtureCredentialStore(),
            database, (_, _) => remote);
        await host.LoadPairsAsync();
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localRoot, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Scaling", "id:scaling"), "Scaling pair", binding.RootNamespaceId) with
        {
            ConnectionId = binding.ConnectionId,
            InitialReviewApproved = false
        };
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            pair.PropagateDeletions ? "recoverable" : "disabled", 0, 1, "manual_preserve_both",
            "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
            RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new FirstSyncEffectChecks.FixtureOperations(host, authority);
        var log = new StringBuilder();
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        SyncAuthorityDatabase.TimingForTest = (name, ms) => { lock (timings) timings[name] = timings.GetValueOrDefault(name) + ms; };
        var watch = Stopwatch.StartNew();
        var prepared = await operations.PrepareFirstSyncAsync(pair.Id, line => log.AppendLine($"{watch.Elapsed.TotalSeconds,8:F2} {line}"),
            CancellationToken.None);
        watch.Stop();
        SyncAuthorityDatabase.TimingForTest = null;
        log.AppendLine("database timings (ms): " + string.Join(", ", timings.OrderByDescending(item => item.Value).Select(item => $"{item.Key}={item.Value:F0}")));
        await File.WriteAllTextAsync(Path.Combine(stateRootBase, "progress-" + size + ".log"), log.ToString());
        await File.WriteAllTextAsync(marker, "");
        if (prepared.TotalItems < size)
            throw new InvalidOperationException($"The benchmark review returned {prepared.TotalItems} items for {size} paths.");
        var seconds = Phases.Select(name => prepared.Phases.Where(item => item.Phase == name)
            .Sum(item => item.ElapsedSeconds)).ToArray();
        SqliteConnection.ClearAllPools();
        var bytes = new FileInfo(database.DatabasePath).Length +
                    (File.Exists(database.DatabasePath + "-wal") ? new FileInfo(database.DatabasePath + "-wal").Length : 0);
        Console.WriteLine($"size {size}: total {watch.Elapsed.TotalSeconds:F1}s; items {prepared.TotalItems}, " +
                          $"local only {prepared.LocalOnlyItems}, remote only {prepared.DropboxOnlyItems}, conflicts {prepared.ConflictItems}; phases: " +
                          string.Join(", ", prepared.Phases.Select(item => $"{item.Phase} {item.ElapsedSeconds:F1}s")));
        return (size, seconds, bytes);
    }
}
