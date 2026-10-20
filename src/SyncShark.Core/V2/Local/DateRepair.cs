using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed record DateRepairExample(string Path, DateTimeOffset OldUtc, DateTimeOffset NewUtc, string Outcome);
public sealed record DateRepairReport(string PairId, bool DryRun, long Examined, long Eligible, long Changed,
    long Skipped, string ReportFile, IReadOnlyList<DateRepairExample> Examples);

/// <summary>Metadata-only repair. No writer or upload API is accepted by this service.</summary>
internal static class DateRepair
{
    internal static (long Start, long End) UtcRange(DateOnly? localDate, DateOnly? from, DateOnly? to,
        TimeZoneInfo zone)
    {
        if (localDate is not null && (from is not null || to is not null))
            throw new SyncCliUsageException("Use either --local-date or --from/--to, not both.");
        from = localDate ?? from;
        to = localDate ?? to;
        if (from > to) throw new SyncCliUsageException("--from must not be later than --to.");
        if (to == DateOnly.MaxValue) throw new SyncCliUsageException("The final local day must be before 9999-12-31.");
        long Midnight(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(
            day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone).Ticks;
        return (from is { } start ? Midnight(start) : 0,
            to is { } end ? Midnight(end.AddDays(1)) : DateTime.MaxValue.Ticks);
    }

    internal static async Task<DateRepairReport> RunAsync(SqliteStateStore store, PairDef pair,
        Func<string, CancellationToken, Task<ItemState?>> remote, string productRoot, bool dryRun,
        IReadOnlyList<string> productOwned, CancellationToken ct, DateOnly? localDate = null, string afterKey = "",
        Func<Func<Task>, Task>? withPageLock = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, ItemState?>>>? remotePage = null,
        DateOnly? from = null, DateOnly? to = null)
    {
        var side = pair.A.Provider == SyncProviders.Local ? Side.A : Side.B;
        var localRoot = side == Side.A ? pair.A : pair.B;
        var cloudRoot = side == Side.A ? pair.B : pair.A;
        if (localRoot.Provider != SyncProviders.Local || cloudRoot.Provider != SyncProviders.Dropbox)
            throw new SyncCliUsageException("Date repair needs one local and one Dropbox side. Choose a local/Dropbox pair.");
        var includes = PairInclude.ForAction(pair, side, store.Pairs(), productOwned);
        var ignores = PairInclude.IgnoreRules(pair);
        var identity = LocalItems.ParseObjectId(localRoot.RootObjectId);
        using var root = PilotDirectoryLease.OpenDirectoryReadGuard(localRoot.RootPath,
            new PilotDirectoryIdentity(identity.Volume, identity.FileId));
        var directory = Path.Combine(productRoot, "v2", "logs");
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, $"dates-repair-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.jsonl");
        using var logFile = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(logFile) { AutoFlush = true };
        var logGate = new object();
        void Record(object row)
        {
            lock (logGate) { log.WriteLine(JsonSerializer.Serialize(row)); }
        }
        long examined = 0, eligible = 0, changed = 0, skipped = 0;
        var examples = new List<DateRepairExample>(10);
        var after = afterKey;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var finished = false;
            var pageStart = after;
            async Task ProcessPage()
            {
                var currentPair = store.Pair(pair.PairId);
                if (System.Text.Json.JsonSerializer.Serialize(currentPair) != System.Text.Json.JsonSerializer.Serialize(pair))
                    throw new IOException("Pair settings changed. Restart dates repair using the current pair settings.");
                var page = store.DateRepairPage(pair.PairId, after, side: side, localDate: localDate, from: from, to: to);
                if (page.Count == 0) { finished = true; return; }
                store.PrepareDateRepair(pair.PairId);
                var paths = page.Select(b => (side == Side.A ? b.B : b.A).Path).ToArray();
                IReadOnlyDictionary<string, ItemState?> metadata;
                if (remotePage is not null) metadata = await remotePage(paths, ct);
                else
                {
                    var rows = new System.Collections.Concurrent.ConcurrentDictionary<string, ItemState?>();
                    await Parallel.ForEachAsync(paths.Distinct(), new ParallelOptions
                        { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (path, token) => rows[path] = await remote(path, token));
                    metadata = rows;
                }
                // One durable intent batch before any file date changes; completion/skip records flush once per page.
                Record(new { Outcome = "page-intent", After = pageStart, Items = page.Select(b => new {
                    Path = (side == Side.A ? b.A : b.B).Path, OldUtc = store.Live(localRoot.RootId, [(side == Side.A ? b.A : b.B).Path]).SingleOrDefault()?.Mtime,
                    NewUtc = metadata[(side == Side.A ? b.B : b.A).Path]?.Mtime }).ToArray() });
                logFile.Flush(true);
                await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (baseline, token) =>
                {
                    var saved = side == Side.A ? baseline.A : baseline.B;
                    var local = store.Live(localRoot.RootId, [saved.Path]).SingleOrDefault() ?? saved;
                    var cloud = side == Side.A ? baseline.B : baseline.A;
                    if (local.Kind != ItemKind.File) return;
                    Interlocked.Increment(ref examined);
                    try
                    {
                        if (!includes(local.Path)) throw new IOException("Excluded by pair selection.");
                        if (SyncIgnorePolicy.IsIgnored(local.Path, false, ignores)) throw new IOException("Ignored by rule.");
                        if (store.DateRepairHeld(pair, baseline)) throw new IOException("Held or quarantined.");
                        if (local.Hash is null) throw new IOException("Local inventory has no content hash.");
                        if (local.Hash != saved.Hash || local.Size != saved.Size)
                            throw new IOException("Local inventory content differs from the baseline.");
                        var current = metadata[cloud.Path];
                        if (current is null || current.Kind != ItemKind.File || current.Hash != local.Hash ||
                            current.ObjectId != cloud.ObjectId || current.Size != local.Size)
                            throw new IOException("Dropbox no longer matches the baseline.");
                        if (current.Mtime < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) ||
                            current.Mtime > DateTimeOffset.UtcNow.AddDays(1))
                            throw new IOException("Dropbox client_modified is implausible; date left unchanged.");
                        // Only the newer local download stamp is repaired, never the opposite direction.
                        if (local.Mtime - current.Mtime < TimeSpan.FromDays(1)) return;
                        _ = PilotPath.NormalizeRelative(local.Path);
                        if (!PilotPath.IsPathInside(localRoot.RootPath, LocalItems.FullPath(localRoot.RootPath, local.Path)))
                            throw new IOException("Path is outside the local root.");
                        using var ancestors = LocalItems.GuardRefreshPaths(localRoot.RootPath, localRoot.RootId, store, [local.Path]);
                        if (ancestors.Safe.Count != 1) throw new IOException("Unsafe or missing ancestor.");
                        root.VerifyBinding();
                        var full = LocalItems.FullPath(localRoot.RootPath, local.Path);
                        if (PilotPath.IsUnsafeAttributes(File.GetAttributes(full))) throw new IOException("Link or placeholder.");
                        // Open-existing and no sharing: neither content writes nor a rename can race this repair.
                        using var file = new FileStream(full, FileMode.Open, FileAccess.ReadWrite,
                            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (PilotPath.IsUnsafePath(full) || !PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info))
                            throw new IOException("Unsafe or unreadable file identity.");
                        if (info.NumberOfLinks > 1) throw new IOException("Hard-linked file; changing its date would affect another path.");
                        var objectId = SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber,
                            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                        var old = new DateTimeOffset(File.GetLastWriteTimeUtc(file.SafeFileHandle), TimeSpan.Zero);
                        if (objectId != local.ObjectId || file.Length != local.Size || (old != local.Mtime && old != current.Mtime) ||
                            await DropboxContentHash.ComputeAsync(file, 65536, token).ConfigureAwait(false) != local.Hash)
                            throw new IOException("Local file changed since its inventory observation.");
                        token.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref eligible);
                        var example = new DateRepairExample(local.Path, old, current.Mtime, dryRun ? "would-repair" : "intent");
                        Record(example); // covered by the durable page-intent batch above
                        if (!dryRun)
                        {
                            var observed = store.RepairDate(baseline, side, current.Mtime, time =>
                            {
                                if (File.GetLastWriteTimeUtc(file.SafeFileHandle) != time.UtcDateTime)
                                    File.SetLastWriteTimeUtc(file.SafeFileHandle, time.UtcDateTime);
                                return new DateTimeOffset(File.GetLastWriteTimeUtc(file.SafeFileHandle), TimeSpan.Zero);
                            }, old, local);
                            Interlocked.Increment(ref changed);
                            example = example with { Outcome = "repaired", NewUtc = observed };
                            Record(example);
                        }
                        lock (logGate) if (examples.Count < 10) examples.Add(example);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Interlocked.Increment(ref skipped);
                        Record(new { Path = local.Path, Outcome = "skipped", Reason = error.Message });
                    }
                }).ConfigureAwait(false);
                after = page[^1].ObjectKey;
                Record(new { Outcome = "page-complete", After = after });
                logFile.Flush(true);
            }
            try
            {
                if (withPageLock is null) await ProcessPage();
                else await withPageLock(ProcessPage);
            }
            catch (Exception error)
            {
                Record(new { Outcome = "aborted", After = pageStart, Examined = examined, Eligible = eligible,
                    Changed = changed, Skipped = skipped, Reason = error.Message,
                    Resume = $"dates repair --pair {pair.PairId}" + (dryRun ? " --dry-run" : " --yes") +
                        (localDate is { } date ? $" --local-date {date:yyyy-MM-dd}" : "") +
                        (from is { } first ? $" --from {first:yyyy-MM-dd}" : "") +
                        (to is { } last ? $" --to {last:yyyy-MM-dd}" : "") +
                        (pageStart.Length > 0 ? $" --after \"{pageStart}\"" : "") });
                logFile.Flush(true);
                Console.Error.WriteLine($"Date repair stopped. Resume instructions and page cursor are in {reportPath}");
                throw;
            }
            if (finished) break;
            await Task.Yield(); // release the page gate before an independent queued sync is admitted
        }
        var result = new DateRepairReport(pair.PairId, dryRun, examined, eligible, changed, skipped, reportPath, examples);
        Record(result);
        logFile.Flush(true);
        return result;
    }
}
