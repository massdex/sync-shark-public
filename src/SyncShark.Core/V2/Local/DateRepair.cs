using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed record DateRepairExample(string Path, DateTimeOffset OldUtc, DateTimeOffset NewUtc, string Outcome)
{
    public string? Evidence { get; init; }
    public string? Hash { get; init; }
    public string? ObjectId { get; init; }
    public string? ObjectKey { get; init; }
    public long Size { get; init; }
}
public sealed record DateRepairReport(string PairId, bool DryRun, long Examined, long Eligible, long Changed,
    long Skipped, string ReportFile, IReadOnlyList<DateRepairExample> Examples)
{
    public long MissingWriteHistory { get; init; }
    public long LocalDateNotWritten { get; init; }
    public long Mismatches { get; init; }
    public string? CsvFile { get; init; }
}

/// <summary>Metadata-only repair. No writer or upload API is accepted by this service.</summary>
internal static partial class DateRepair
{
    internal static (long Start, long End) UtcRange(DateOnly? localDate, DateRepairBound? from, DateRepairBound? to,
        TimeZoneInfo zone)
    {
        if (localDate is not null && (from is not null || to is not null))
            throw new SyncCliUsageException("Use either --local-date or --from/--to, not both.");
        if (localDate is { } day) from = to = (DateRepairBound)day;
        DateTime? start = from?.Local;
        DateTime? end = to?.Local;
        if (to is { WholeDay: true })
        {
            if (to.Local.Date == DateTime.MaxValue.Date)
                throw new SyncCliUsageException("The final local day must be before 9999-12-31.");
            end = to.Local.AddDays(1);
        }
        if (start >= end) throw new SyncCliUsageException("--from must be earlier than the exclusive end of --to.");
        long Utc(DateTime value)
        {
            if (zone.IsInvalidTime(value) || zone.IsAmbiguousTime(value))
                throw new SyncCliUsageException("The local boundary is skipped or repeated by daylight saving. Choose an unambiguous local time.");
            return TimeZoneInfo.ConvertTimeToUtc(value, zone).Ticks;
        }
        return (start is { } first ? Utc(first) : 0, end is { } last ? Utc(last) : DateTime.MaxValue.Ticks);
    }

    internal static bool ItemFailure(Exception error) => error is not OperationCanceledException &&
        error is not OutOfMemoryException && error is not StateDamagedException && error is not SyncCliBusyException &&
        error is not Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 11 or 26 };

    internal static async Task<DateRepairReport> RunAsync(SqliteStateStore store, PairDef pair,
        Func<string, CancellationToken, Task<ItemState?>> remote, string productRoot, bool dryRun,
        IReadOnlyList<string> productOwned, CancellationToken ct, DateOnly? localDate = null, string afterKey = "",
        Func<Func<Task>, Task>? withPageLock = null,
        Func<IReadOnlyList<string>, Action<string, Exception>, CancellationToken, Task<IReadOnlyDictionary<string, ItemState?>>>? remotePage = null,
        DateRepairBound? from = null, DateRepairBound? to = null, string? excludeList = null, bool reportMismatch = false,
        IReadOnlyList<DateProvenanceSource>? provenanceSources = null)
    {
        _ = UtcRange(localDate, from, to, TimeZoneInfo.Local);
        store.PrepareDateRepairExclusions(excludeList);
        store.PrepareDateWriteEvidence(pair.PairId, provenanceSources);
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
        var csvPath = reportMismatch ? Path.ChangeExtension(reportPath, ".csv") : null;
        using var csv = csvPath is null ? null : new StreamWriter(new FileStream(csvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        csv?.WriteLine("Path,LocalUtc,DropboxClientModifiedUtc,Provenance");
        static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var logGate = new object();
        void Record(object row)
        {
            lock (logGate) { log.WriteLine(JsonSerializer.Serialize(row)); }
        }
        Record(new { Outcome = "header", PairId = pair.PairId, RootId = localRoot.RootId, RootObjectId = localRoot.RootObjectId });
        logFile.Flush(true);
        long examined = 0, eligible = 0, changed = 0, skipped = 0, missingHistory = 0, notWritten = 0, mismatches = 0;
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
                var page = store.DateRepairPage(pair.PairId, after, side: side, localDate: localDate, from: from, to: to, mismatch: reportMismatch);
                if (page.Count == 0) { finished = true; return; }
                try { store.PrepareDateRepair(pair.PairId); }
                catch (Exception error) when (ItemFailure(error))
                {
                    foreach (var baseline in page)
                    {
                        examined++; skipped++;
                        Record(new { Path = (side == Side.A ? baseline.A : baseline.B).Path,
                            Outcome = "skipped", Reason = "Protection lookup: " + error.Message });
                    }
                    after = page[^1].ObjectKey;
                    Record(new { Outcome = "page-complete", After = after });
                    logFile.Flush(true);
                    return;
                }
                var locals = new Dictionary<string, ItemState?>();
                var localErrors = new Dictionary<string, string>();
                var lookupPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var baseline in page)
                {
                    var path = (side == Side.A ? baseline.A : baseline.B).Path;
                    try
                    {
                        var local = store.Live(localRoot.RootId, [path]).SingleOrDefault();
                        locals[path] = local;
                        if (reportMismatch || (local is not null && store.DateWriteSkipReason(side, local,
                            (side == Side.A ? baseline.A : baseline.B).Hash) is null))
                            lookupPaths.Add((side == Side.A ? baseline.B : baseline.A).Path);
                    }
                    catch (Exception error) when (ItemFailure(error)) { localErrors[path] = error.Message; }
                }
                var paths = lookupPaths.ToArray();
                var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                void Failed(string path, Exception error) => errors[path] = error.Message;
                IReadOnlyDictionary<string, ItemState?> metadata;
                if (paths.Length == 0) metadata = new Dictionary<string, ItemState?>();
                else if (remotePage is not null)
                {
                    try { metadata = await remotePage(paths, Failed, ct); }
                    catch (Exception error) when (ItemFailure(error))
                    {
                        foreach (var path in paths) Failed(path, error);
                        metadata = new Dictionary<string, ItemState?>();
                    }
                }
                else
                {
                    var rows = new System.Collections.Concurrent.ConcurrentDictionary<string, ItemState?>();
                    await Parallel.ForEachAsync(paths.Distinct(), new ParallelOptions
                        { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (path, token) =>
                    {
                        try { rows[path] = await remote(path, token); }
                        catch (Exception error) when (ItemFailure(error)) { Failed(path, error); }
                    });
                    metadata = rows;
                }
                // One durable intent batch before any file date changes; completion/skip records flush once per page.
                Record(new { Outcome = "page-intent", After = pageStart, Items = page.Select(b => new {
                    Path = (side == Side.A ? b.A : b.B).Path, OldUtc = locals.GetValueOrDefault((side == Side.A ? b.A : b.B).Path)?.Mtime,
                    NewUtc = metadata.GetValueOrDefault((side == Side.A ? b.B : b.A).Path)?.Mtime }).ToArray() });
                logFile.Flush(true);
                await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (baseline, token) =>
                {
                    var saved = side == Side.A ? baseline.A : baseline.B;
                    var local = locals.GetValueOrDefault(saved.Path) ?? saved;
                    var cloud = side == Side.A ? baseline.B : baseline.A;
                    if (local.Kind != ItemKind.File) return;
                    Interlocked.Increment(ref examined);
                    string evidence = "none";
                    try
                    {
                        if (localErrors.TryGetValue(local.Path, out var localError)) throw new IOException("Local inventory: " + localError);
                        if (!locals.ContainsKey(local.Path) || locals[local.Path] is null) throw new IOException("Local inventory item is missing.");
                        if (store.DateRepairExcluded(local.Path) || store.DateRepairExcluded(cloud.Path))
                            throw new IOException("Excluded by --exclude-list.");
                        if (!includes(local.Path)) throw new IOException("Excluded by pair selection.");
                        if (SyncIgnorePolicy.IsIgnored(local.Path, false, ignores)) throw new IOException("Ignored by rule.");
                        if (store.DateRepairHeld(pair, baseline)) throw new IOException("Held or quarantined.");
                        var provenance = store.DateWriteSkipReason(side, local, saved.Hash);
                        evidence = store.DateLogCorrelated(local) ? "log" : provenance is null ? "restore-notice" : "none";
                        if (!reportMismatch && provenance is not null)
                        {
                            if (provenance == "reliable Sync Shark local write history unavailable") Interlocked.Increment(ref missingHistory);
                            else Interlocked.Increment(ref notWritten);
                            throw new IOException(provenance);
                        }
                        if (!reportMismatch && saved.Hash is null) throw new IOException("Baseline has no content hash.");
                        if (!reportMismatch && ((local.Hash is not null && Content.SameHash(local.Hash, saved.Hash) != true) || local.Size != saved.Size))
                            throw new IOException("Local inventory content differs from the baseline.");
                        if (errors.TryGetValue(cloud.Path, out var remoteError)) throw new IOException("Dropbox lookup: " + remoteError);
                        var current = metadata.GetValueOrDefault(cloud.Path);
                        if (current is null || current.Kind != ItemKind.File || (!reportMismatch && Content.SameHash(current.Hash, saved.Hash) != true) ||
                            current.ObjectId != cloud.ObjectId || (!reportMismatch && current.Size != local.Size))
                            throw new IOException("Dropbox no longer matches the baseline.");
                        if (current.Mtime < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) ||
                            current.Mtime > DateTimeOffset.UtcNow.AddDays(1))
                            throw new IOException("Dropbox client_modified is implausible; date left unchanged.");
                        // Only the newer local download stamp is repaired, never the opposite direction.
                        if ((reportMismatch ? (local.Mtime - current.Mtime).Duration() : local.Mtime - current.Mtime) < TimeSpan.FromDays(1)) return;
                        _ = PilotPath.NormalizeRelative(local.Path);
                        if (!PilotPath.IsPathInside(localRoot.RootPath, LocalItems.FullPath(localRoot.RootPath, local.Path)))
                            throw new IOException("Path is outside the local root.");
                        using var ancestors = LocalItems.GuardRefreshPaths(localRoot.RootPath, localRoot.RootId, store, [local.Path]);
                        if (ancestors.Safe.Count != 1) throw new IOException("Unsafe or missing ancestor.");
                        root.VerifyBinding();
                        var full = LocalItems.FullPath(localRoot.RootPath, local.Path);
                        if (PilotPath.IsUnsafeAttributes(File.GetAttributes(full))) throw new IOException("Link or placeholder.");
                        // Open-existing and no sharing: neither content writes nor a rename can race this repair.
                        using var file = new FileStream(full, FileMode.Open, reportMismatch ? FileAccess.Read : FileAccess.ReadWrite,
                            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (PilotPath.IsUnsafePath(full) || !PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info))
                            throw new IOException("Unsafe or unreadable file identity.");
                        if (info.NumberOfLinks > 1) throw new IOException("Hard-linked file; changing its date would affect another path.");
                        var objectId = SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber,
                            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                        var old = new DateTimeOffset(File.GetLastWriteTimeUtc(file.SafeFileHandle), TimeSpan.Zero);
                        if (objectId != local.ObjectId || file.Length != local.Size || (old != local.Mtime && old != current.Mtime))
                            throw new IOException("Local file changed since its inventory observation.");
                        var verifiedHash = await DropboxContentHash.ComputeAsync(file, 65536, token).ConfigureAwait(false);
                        if ((!reportMismatch && (Content.SameHash(verifiedHash, saved.Hash) != true || Content.SameHash(verifiedHash, current.Hash) != true)) ||
                            (local.Hash is not null && Content.SameHash(verifiedHash, local.Hash) != true))
                            throw new IOException("Locked file content differs from baseline or Dropbox.");
                        token.ThrowIfCancellationRequested();
                        provenance = store.DateWriteSkipReason(side, local with { Mtime = old }, verifiedHash);
                        if (reportMismatch)
                        {
                            if ((old - current.Mtime).Duration() < TimeSpan.FromDays(1)) return;
                            if (provenance == "reliable Sync Shark local write history unavailable") Interlocked.Increment(ref missingHistory);
                            else if (provenance is not null) Interlocked.Increment(ref notWritten);
                            Interlocked.Increment(ref mismatches);
                            evidence = store.DateLogCorrelated(local with { Mtime = old }) ? "log-correlated" : provenance is null ? "restore-notice" : "no evidence";
                            lock (logGate) csv!.WriteLine(string.Join(",", Cell(local.Path), Cell(old.ToString("O")),
                                Cell(current.Mtime.ToString("O")), Cell(evidence)));
                            Record(new { Path = local.Path, OldUtc = old, NewUtc = current.Mtime, Outcome = "mismatch", Evidence = evidence, Reason = provenance });
                            return;
                        }
                        // An already-stamped crash survivor needs only the database reconcile; original observed
                        // date was proven above. A genuinely changed date remains protected by the held-file checks.
                        if (provenance is not null && old != current.Mtime) throw new IOException(provenance);
                        Interlocked.Increment(ref eligible);
                        var example = new DateRepairExample(local.Path, old, current.Mtime, dryRun ? "would-repair" : "intent")
                        { Evidence = evidence, Hash = verifiedHash, ObjectId = objectId, ObjectKey = baseline.ObjectKey, Size = file.Length };
                        Record(example);
                        // Persist the actual held-handle OldUtc, including a crash survivor whose inventory is stale.
                        lock (logGate) logFile.Flush(true);
                        if (!dryRun)
                        {
                            var observed = store.RepairDate(baseline, side, current.Mtime, time =>
                            {
                                if (File.GetLastWriteTimeUtc(file.SafeFileHandle) != time.UtcDateTime)
                                    File.SetLastWriteTimeUtc(file.SafeFileHandle, time.UtcDateTime);
                                return new DateTimeOffset(File.GetLastWriteTimeUtc(file.SafeFileHandle), TimeSpan.Zero);
                            }, old, local, verifiedHash);
                            Interlocked.Increment(ref changed);
                            example = example with { Outcome = "repaired", NewUtc = observed };
                            Record(example);
                        }
                        lock (logGate) if (examples.Count < 10) examples.Add(example);
                    }
                    catch (Exception error) when (ItemFailure(error))
                    {
                        Interlocked.Increment(ref skipped);
                        Record(new { Path = local.Path, Outcome = "skipped", Evidence = evidence, Reason = error.Message });
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
            catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteErrorCode is 5 or 6)
            {
                Record(new { Outcome = "waiting", After = pageStart, Reason = "Store temporarily busy: " + error.Message });
                logFile.Flush(true);
                await Task.Delay(250, ct); // no candidate cursor is lost when a page cannot yet be read
                continue;
            }
            catch (Exception error)
            {
                Record(new { Outcome = "aborted", After = pageStart, Examined = examined, Eligible = eligible,
                    Changed = changed, Skipped = skipped, Reason = error.Message,
                    Resume = $"dates {(reportMismatch ? "report-mismatch" : "repair")} --pair {pair.PairId}" + (reportMismatch ? "" : dryRun ? " --dry-run" : " --yes") +
                        (localDate is { } date ? $" --local-date {date:yyyy-MM-dd}" : "") +
                        (from is { } first ? $" --from {first}" : "") +
                        (to is { } last ? $" --to {last}" : "") +
                        (excludeList is not null ? $" --exclude-list \"{Path.GetFullPath(excludeList)}\"" : "") +
                        string.Concat((provenanceSources ?? []).Select(source => $" --provenance-log \"{source.File}\" --provenance-prefix \"{(source.Prefix.Length == 0 ? "." : source.Prefix)}\"")) +
                        (pageStart.Length > 0 ? $" --after \"{pageStart}\"" : "") });
                logFile.Flush(true);
                Console.Error.WriteLine($"Date repair stopped: {error.Message}. Resume instructions and page cursor are in {reportPath}");
                throw;
            }
            if (finished) break;
            await Task.Yield(); // release the page gate before an independent queued sync is admitted
        }
        csv?.Flush();
        var result = new DateRepairReport(pair.PairId, dryRun, examined, eligible, changed, skipped, reportPath, examples)
        { MissingWriteHistory = missingHistory, LocalDateNotWritten = notWritten, Mismatches = mismatches, CsvFile = csvPath };
        Record(result);
        logFile.Flush(true);
        return result;
    }
}
