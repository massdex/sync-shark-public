using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class DateRepair
{
    internal static string AuditPair(string audit)
    {
        using var lines = File.ReadLines(audit).GetEnumerator();
        if (!lines.MoveNext()) throw new SyncCliUsageException("The audit log is empty.");
        using var header = JsonDocument.Parse(lines.Current);
        if (!header.RootElement.TryGetProperty("Outcome", out var outcome) || outcome.GetString() != "header" ||
            !header.RootElement.TryGetProperty("PairId", out var pair))
            throw new SyncCliUsageException("This audit lacks restore identity information. Dates were not changed.");
        return pair.GetString() ?? throw new SyncCliUsageException("Audit pair is missing.");
    }

    internal static async Task<DateRepairReport> RestoreAsync(SqliteStateStore store, PairDef pair, string audit,
        string productRoot, bool dryRun, IReadOnlyList<string> productOwned, CancellationToken ct, Func<Exception, bool> isStateDamage, Func<Func<Task>, Task>? withPageLock = null)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        if (AuditPair(audit) != pair.PairId) throw new SyncCliUsageException("Audit belongs to another pair.");
        var side = pair.A.Provider == SyncProviders.Local ? Side.A : Side.B;
        var localRoot = side == Side.A ? pair.A : pair.B;
        if (localRoot.Provider != SyncProviders.Local) throw new SyncCliUsageException("Restore requires a local side.");
        using (var header = JsonDocument.Parse(File.ReadLines(audit).First()))
        {
            if (header.RootElement.GetProperty("RootId").GetString() != localRoot.RootId ||
                header.RootElement.GetProperty("RootObjectId").GetString() != localRoot.RootObjectId)
                throw new SyncCliUsageException("Audit local root identity changed.");
        }
        var identity = LocalItems.ParseObjectId(localRoot.RootObjectId);
        using var root = PilotDirectoryLease.OpenDirectoryReadGuard(localRoot.RootPath,
            new PilotDirectoryIdentity(identity.Volume, identity.FileId));
        var includes = PairInclude.ForAction(pair, side, store.Pairs(), productOwned);
        var ignores = PairInclude.IgnoreRules(pair);
        var directory = Path.Combine(productRoot, "v2", "logs");
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, $"dates-restore-{Guid.NewGuid():N}.jsonl");
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(output) { AutoFlush = true };
        var logGate = new object();
        void Record(object row) { lock (logGate) { log.WriteLine(JsonSerializer.Serialize(row)); output.Flush(true); } }
        long examined = 0, eligible = 0, changed = 0, skipped = 0;
        var examples = new List<DateRepairExample>();
        async Task ProcessLine(string line)
        {
            ct.ThrowIfCancellationRequested();
            string? path = null;
            try
            {
                using var json = JsonDocument.Parse(line);
                var row = json.RootElement;
                if (!row.TryGetProperty("Outcome", out var outcome)) return;
                if (outcome.GetString() == "header")
                {
                    if (row.GetProperty("RootId").GetString() != localRoot.RootId ||
                        row.GetProperty("RootObjectId").GetString() != localRoot.RootObjectId)
                        throw new SyncCliUsageException("Audit local root identity changed.");
                    return;
                }
                // Intent-only records cannot establish the committed, filesystem-rounded repaired time.
                if (outcome.GetString() != "repaired") return;
                Interlocked.Increment(ref examined);
                var entry = JsonSerializer.Deserialize<DateRepairExample>(line)!;
                path = entry.Path;
                if (entry.Hash is null || entry.ObjectId is null || entry.ObjectKey is null)
                    throw new IOException("Audit lacks verified content or identity; left unchanged.");
                var baseline = store.Baselines(pair.PairId, [entry.ObjectKey]).SingleOrDefault()
                    ?? throw new IOException("Baseline no longer exists.");
                var saved = side == Side.A ? baseline.A : baseline.B;
                if (saved.Path != path || Content.SameHash(saved.Hash, entry.Hash) != true || saved.Mtime != entry.NewUtc)
                    throw new IOException("Baseline changed since repair.");
                var local = store.Live(localRoot.RootId, [path]).SingleOrDefault()
                    ?? throw new IOException("Local inventory is missing.");
                if (!includes(path) || SyncIgnorePolicy.IsIgnored(path, false, ignores) || store.DateRepairHeld(pair, baseline))
                    throw new IOException("Excluded, ignored, held or quarantined.");
                _ = PilotPath.NormalizeRelative(path);
                var full = LocalItems.FullPath(localRoot.RootPath, path);
                if (!PilotPath.IsPathInside(localRoot.RootPath, full)) throw new IOException("Path outside root.");
                using var ancestors = LocalItems.GuardRefreshPaths(localRoot.RootPath, localRoot.RootId, store, [path]);
                if (ancestors.Safe.Count != 1 || PilotPath.IsUnsafePath(full)) throw new IOException("Unsafe ancestor or file.");
                root.VerifyBinding();
                using var file = new FileStream(full, FileMode.Open, dryRun ? FileAccess.Read : FileAccess.ReadWrite,
                    FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info) || info.NumberOfLinks > 1)
                    throw new IOException("Unsafe file identity or hard link.");
                var objectId = SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                if (objectId != entry.ObjectId || objectId != local.ObjectId || file.Length != entry.Size || local.Size != entry.Size ||
                    (local.Hash is not null && Content.SameHash(local.Hash, entry.Hash) != true) ||
                    File.GetLastWriteTimeUtc(file.SafeFileHandle) != entry.NewUtc.UtcDateTime)
                    throw new IOException("File identity, size or date changed since repair.");
                var hash = await DropboxContentHash.ComputeAsync(file, 65536, ct).ConfigureAwait(false);
                if (Content.SameHash(hash, entry.Hash) != true) throw new IOException("File content changed since repair.");
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref eligible);
                var example = entry with { OldUtc = entry.NewUtc, NewUtc = entry.OldUtc, Outcome = dryRun ? "would-restore" : "restore-intent" };
                Record(example);
                if (!dryRun)
                {
                    var observed = store.RepairDate(baseline, side, entry.OldUtc, time =>
                    {
                        File.SetLastWriteTimeUtc(file.SafeFileHandle, time.UtcDateTime);
                        return new DateTimeOffset(File.GetLastWriteTimeUtc(file.SafeFileHandle), TimeSpan.Zero);
                    }, entry.NewUtc, local, hash);
                    Interlocked.Increment(ref changed);
                    example = example with { NewUtc = observed, Outcome = "restored" };
                    Record(example);
                }
                lock (logGate) if (examples.Count < 10) examples.Add(example);
            }
            catch (Exception error) when (ItemFailure(error, isStateDamage) && error is not SyncCliUsageException)
            {
                Interlocked.Increment(ref skipped); Record(new { Path = path, Outcome = "skipped", Reason = error.Message });
            }
        }
        foreach (var page in File.ReadLines(audit).Chunk(128))
        {
            async Task ProcessPage()
            {
                if (JsonSerializer.Serialize(store.Pair(pair.PairId)) != JsonSerializer.Serialize(pair))
                    throw new IOException("Pair settings changed. Restart dates restore.");
                store.PrepareDateRepair(pair.PairId);
                await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (line, _) => await ProcessLine(line)).ConfigureAwait(false);
            }
            if (withPageLock is null) await ProcessPage();
            else await withPageLock(ProcessPage);
            await Task.Yield();
        }
        var result = new DateRepairReport(pair.PairId, dryRun, examined, eligible, changed, skipped, reportPath, examples);
        Record(result);
        return result;
    }
}
