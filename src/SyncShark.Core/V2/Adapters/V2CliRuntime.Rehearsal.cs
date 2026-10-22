using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    /// <summary>Runs the ordinary pipeline against virtual endpoints and an explicitly isolated store.</summary>
    public async Task<object> RehearseAsync(string pairId, string exportPath, int runs,
        Action<string> progress, CancellationToken ct, string? provenancePath = null)
    {
        if (!_planOnlyIsolated) throw new SyncCliUnsupportedException("Rehearsal requires an isolated --data-root.");
        if (runs is < 1 or > 100) throw new SyncCliUsageException("--runs must be from 1 to 100.");
        var pair = RequirePair(pairId);
        RejectDataRootInsidePair(pair);
        var export = ValidatePlanExportPath(exportPath, _store.Pairs(), _productRoot);
        var provenanceExport = provenancePath is null ? null : ValidatePlanExportPath(provenancePath, _store.Pairs(), _productRoot);
        if (provenanceExport is not null && string.Equals(export, provenanceExport, StringComparison.OrdinalIgnoreCase))
            throw new SyncCliUsageException("--plan-provenance requires a separate new CSV file.");
        var priorProvenance = PlanProvenance.Current.Value;
        await WaitForDataLockAsync(_runGate, progress, ct);
        try
        {
            using var stream = new FileStream(export, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            WriteCsvRow(log, "run", "kind", "side", "provider", "path", "size", "hash");
            using var provenanceWriter = provenanceExport is null ? null : new StreamWriter(
                new FileStream(provenanceExport, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            using var provenance = provenanceWriter is null ? null : new PlanProvenance(
                Path.Combine(_productRoot, "v2", "provenance-" + Guid.NewGuid().ToString("N") + ".db"), provenanceWriter);
            PlanProvenance.Current.Value = provenance;
            var current = 0;
            var logGate = new object();
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            using var overlay = new RehearsalOverlay(Path.Combine(_productRoot, "v2", "rehearsal-" + Guid.NewGuid().ToString("N") + ".db"));
            await using var endpoints = new RehearsalEndpoints(_run.RehearsalSource, overlay, effect =>
            {
                lock (logGate)
                {
                    var target = effect.Side == "A" ? Side.A : Side.B;
                    var direction = effect.Kind is "CopyNew" or "Update" or "KeepBoth"
                        ? SideRole(pair, target == Side.A ? Side.B : Side.A) + "-to-" + SideRole(pair, target)
                        : SideRole(pair, target);
                    var key = effect.Kind + ":" + direction;
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    WriteCsvRow(log, current.ToString(CultureInfo.InvariantCulture), effect.Kind, effect.Side,
                        effect.Provider, effect.Path, effect.Size.ToString(CultureInfo.InvariantCulture), effect.Hash);
                }
            }, _store);
            var run = _run.Rehearsal(endpoints);
            var summaries = new List<object>();
            var failed = false;
            var pendingIntents = _store.CountUnresolvedIntents(pairId);
            var unsupportedReconciles = _store.StreamUnresolvedIntents(pairId).Count(PlanOnlyEndpoints.UnsupportedReconcile)
                + PendingRestorePreviewNotices(pairId).Count();
            progress($"Copied store: {pendingIntents} unfinished intents; {unsupportedReconciles} unsupported reconciles/restores (not an exact replay).");
            progress("REHEARSAL: no endpoint writes are performed on your disk or Dropbox");
            progress("Only the isolated store and explicit diagnostic files are written. Simulated content is metadata-only.");
            const string notSimulated = "online-only pointer updates/publication; real transfer bytes and hash verification; " +
                "provider batch-job/change-feed race and restoration machinery; retained checkpoint reconciliation; " +
                "dispatch-time source revalidation (safety2 A1/A2; may conservatively over-count deletes that live would skip); " +
                "whole-folder restore classification / send-boundary (safety3); " +
                "provider resource-budget timing; cumulative endpoint disk/quota consumption and native crash durability";
            progress("not simulated: " + notSimulated);
            progress("Phase timings are diagnostic only: overlay writes are serialized and no payload bytes are transferred.");
            for (current = 1; current <= runs; current++)
            {
                counts.Clear();
                provenance?.BeginRun(current);
                var phases = new RehearsalProgress(progress);
                var elapsed = Stopwatch.StartNew();
                RunReport report;
                try { report = await run.IncrementalAsync(pairId, phases, ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    phases.Finish();
                    summaries.Add(new { Run = current, Outcome = "Failed", Failure = SyncCli.DescribeException(error),
                        Counts = new Dictionary<string, long>(counts), PhaseMilliseconds = phases.Times,
                        ElapsedMilliseconds = elapsed.ElapsedMilliseconds });
                    throw new SyncCliOperationException("Rehearsal threw; the partial effect log and isolated state are retained.",
                        error, new { PairId = pairId, EffectLog = export, PlanProvenance = provenanceExport, PendingIntents = pendingIntents,
                UnsupportedReconciles = unsupportedReconciles, NotSimulated = notSimulated, Runs = summaries });
                }
                phases.Finish();
                elapsed.Stop();
                provenance?.Summary(progress);
                failed |= report.Failed > 0 || report.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase);
                summaries.Add(new { Run = current, Report = report, Counts = new Dictionary<string, long>(counts),
                    PhaseMilliseconds = phases.Times, ElapsedMilliseconds = elapsed.ElapsedMilliseconds });
                progress($"Rehearsal {current}/{runs}: {report.Outcome}; done {report.Done}, uploaded {report.Uploaded}, downloaded {report.Downloaded}, deleted {report.Deleted}, failed {report.Failed}; {elapsed.Elapsed}.");
                progress("Effects by kind/direction: " + string.Join(", ", counts.OrderBy(row => row.Key).Select(row => row.Key + "=" + row.Value)));
                progress("Phase milliseconds: " + string.Join(", ", phases.Times.Select(row => row.Key + "=" + row.Value)));
            }
            var result = new { PairId = pairId, EffectLog = export, PlanProvenance = provenanceExport, PendingIntents = pendingIntents,
                UnsupportedReconciles = unsupportedReconciles, NotSimulated = notSimulated, Runs = summaries };
            if (failed) throw new SyncCliReviewException("At least one rehearsal run failed; inspect its notices and effect log.", result);
            return result;
        }
        finally { PlanProvenance.Current.Value = priorProvenance; _runGate.Release(); }
    }

    private sealed class RehearsalProgress(Action<string> output) : IProgress<RunProgress>
    {
        private readonly object _gate = new();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private string _phase = "starting";
        public Dictionary<string, long> Times { get; } = new(StringComparer.Ordinal);
        public void Report(RunProgress value)
        {
            lock (_gate)
            {
                if (_phase != value.Phase) { Finish(); _phase = value.Phase; }
                output(value.Message ?? $"{value.Phase}: {value.Done}/{value.Total} {value.Path}");
            }
        }
        public void Finish()
        {
            lock (_gate) { Times[_phase] = Times.GetValueOrDefault(_phase) + _watch.ElapsedMilliseconds; _watch.Restart(); }
        }
    }
}
