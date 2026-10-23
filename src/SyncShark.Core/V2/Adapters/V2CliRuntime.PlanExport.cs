using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.V2;

// Bounded CLI plan preview. The shared incremental planner runs against an explicitly isolated product folder
// and its plan is streamed to one CSV file. Nothing is executed and no endpoint content is changed; the
// preparation's scan cursors and retry bookkeeping are retained only inside that isolated folder, never in the
// live product state.
public sealed partial class V2CliRuntime
{
    private const string DataRootOption = "--data-root";
    // Set only by AcquireIsolatedPlanPreview, so a plan-only export can never run against a live or borrowed
    // runtime even if another caller reaches this method directly.
    private bool _planOnlyIsolated;

    private static readonly string[] PlanCsvColumns =
    [
        "record", "actionId", "disposition", "kind", "targetSide", "direction", "path", "fromPath", "rule",
        "recoveryFirst", "sourceObjectId", "sourceVersionId", "sourceHash", "sourceSize", "sourceMtime",
        "targetObjectId", "targetVersionId", "targetHash", "targetSize", "targetMtime", "detail"
    ];

    /// <summary>
    /// Previews the pair's shared incremental plan under the run gate and streams it to an explicit CSV file.
    /// The runtime was opened against an isolated data folder, so the live store is never read or written.
    /// </summary>
    public async Task<SyncCliPlanExportData> PreviewPlanExportAsync(string pairId, string exportPath,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        if (!_planOnlyIsolated)
            throw new SyncCliUnsupportedException("A plan-only export requires an isolated data folder and is " +
                "available only through run --plan-only with --data-root.");
        // The isolated store is open at this point, so the pair definitions are known: an isolated folder inside
        // a paired tree would let the store's own files be scanned as pair content, so it is refused before any
        // planning or provider access.
        RejectDataRootInsidePair(pair);
        var export = ValidatePlanExportPath(exportPath, _store.Pairs(), _productRoot);
        return await _run.WithPairAdmissionAsync(pairId, async () =>
        {
            var unsupported = _store.UnresolvedIntents(pairId).Where(PlanOnlyEndpoints.UnsupportedReconcile).ToArray();
            SyncRun.IncrementalPlanPreview preview;
            try
            {
                preview = await _run.PreviewIncrementalAsync(pairId, new RunProgressForwarder(progress),
                    cancellationToken);
            }
            catch (SyncRunRefusedException error)
            {
                throw new SyncCliReviewException(error.Message);
            }
            catch (EndpointAccessException error)
            {
                throw new SyncCliReviewException(ReconnectText(error));
            }
            var plan = preview.Plan;
            // A plan that could not be completed is never written as a complete export.
            if (plan.RunUnavailable is { } unavailable)
                throw new SyncRunPendingVerificationException(unavailable, 30);
            var pendingRestores = PendingRestorePreviewNotices(pairId).Count();
            var rows = WritePlanCsv(export, pair, plan, unsupported, cancellationToken, preview.Unapproved);
            if (pendingRestores > 0)
                progress($"Pending recovery restores: {pendingRestores}. Restore retries are not performed read-only; inspect RESTORE rows.");
            progress("Each preview consumes its isolated copy. Stop the app and make a fresh copy of the database together with its -wal and -shm files before every preview.");
            progress($"Unsupported read-only reconciliation: {unsupported.Length}." +
                (unsupported.Length == 0 ? "" : " Export is not an exact execution plan; inspect UNSUPPORTED-RECONCILE rows."));
            return new SyncCliPlanExportData(pair.PairId, pair.Name, export, plan.Gen, plan.Actions.Count,
                plan.DeferredPlanningActions.Count, plan.SkippedActions.Count,
                plan.HeldDeletions.Count + plan.UnreviewedHeldDeletions.Count + preview.Unapproved.Count, plan.RecoveryPending.Count + pendingRestores,
                plan.Notices.Count, _store.CountUnresolvedIntents(pairId), rows)
            { UnsupportedReconciles = unsupported.Length + pendingRestores };
        }, cancellationToken, progress);
    }

    // Opens the plan-only runtime against an explicit isolated folder. The checks run before the exclusive
    // lock and the store, so a refused data root never opens, creates or writes product state.
    internal static V2CliRuntime AcquireIsolatedForTest(string[] args, Func<bool> contentPaused)
    {
        var invocation = SyncCliParser.Parse(args);
        if (invocation.Command != "run" || !(invocation.Flag("--plan-only") || invocation.Flag("--rehearse")))
            throw new ArgumentException("Fixture activity policy requires an isolated preview or rehearsal.", nameof(args));
        return AcquireIsolatedPlanPreview(invocation, contentPaused);
    }

    private static V2CliRuntime AcquireIsolatedPlanPreview(SyncCliInvocation invocation) => AcquireIsolatedPlanPreview(invocation, null);
    private static V2CliRuntime AcquireIsolatedPlanPreview(SyncCliInvocation invocation, Func<bool>? contentPausedForTest)
    {
        var productRoot = ValidateIsolatedDataRoot(invocation.Value(DataRootOption) ?? string.Empty);
        var lockPath = Path.Combine(productRoot, "state", "instance.lock");
        PilotInstanceLock instanceLock;
        try
        {
            instanceLock = PilotInstanceLock.Acquire(lockPath);
        }
        catch (IOException error) when (SyncCliRuntime.IsLockContention(error))
        {
            throw new SyncCliBusyException(
                "The isolated data folder named by --data-root is already in use by another Sync Shark process, " +
                "so this plan-only preview cannot open it. Close that process or choose another isolated folder.", error);
        }
        catch (Exception error)
        {
            throw new SyncCliOperationException(
                "The isolated product state named by --data-root could not be opened: " +
                SyncCli.DescribeException(error), error);
        }
        try
        {
            var composition = EngineComposition.Open(
                new EngineHostInputs(productRoot, instanceLock.StateAuthority, SharedHttp)
                    { ContentPausedForTest = contentPausedForTest }, V2Settings.Default,
                TimeProvider.System);
            return new V2CliRuntime(instanceLock, composition.Store, composition.Run, composition.Roots, productRoot)
            {
                _composition = composition,
                _planOnlyIsolated = true
            };
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    // Compatibility wrappers retain reflection-based fixture entry points; all policy lives in CliExportPaths.
    internal static string ValidateIsolatedDataRoot(string requested, string? liveProductRoot = null) =>
        CliExportPaths.ValidateIsolatedDataRoot(requested, liveProductRoot);

    internal static string ValidatePlanExportPath(string exportPath, IReadOnlyList<PairDef> pairs,
        string isolatedRoot, string? liveProductRoot = null) =>
        CliExportPaths.ValidatePlanExportPath(exportPath, pairs, isolatedRoot, liveProductRoot);

    private void RejectDataRootInsidePair(PairDef pair) =>
        CliExportPaths.RejectDataRootInsidePair(_productRoot, pair);

    // One flat CSV table: every planned action, then the plan's deferred, skipped, held and pending markers, so a
    // reader sees exactly what was planned and what was deliberately not planned. The write is all-or-nothing: a
    // uniquely named sibling partial is created new and moved into place only after every row was written.
    internal int WritePlanCsv(string path, PairDef pair, Plan plan, IReadOnlyList<SyncAction> unsupported,
        CancellationToken cancellationToken, IReadOnlyList<string>? unapproved = null)
    {
        if (plan.RunUnavailable is { } unavailable)
            throw new SyncRunPendingVerificationException(unavailable, 30);
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        var rows = 0;
        var created = false;
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65_536))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                   {
                       NewLine = "\r\n"
                   })
            {
                created = true;
                WriteCsvRow(writer, PlanCsvColumns);
                foreach (var action in plan.Actions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteActionRow(writer, pair, action, action.Kind == ActionKind.Hold ? "held" : "planned");
                    rows++;
                }
                foreach (var action in plan.DeferredPlanningActions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteActionRow(writer, pair, action, "deferred");
                    rows++;
                }
                foreach (var action in plan.SkippedActions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteActionRow(writer, pair, action, "skipped");
                    rows++;
                }
                foreach (var held in plan.HeldDeletions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMarkerRow(writer, "held-deletion", "held", held.Local, held.Path, held.Rule,
                        "Deletion evidence is pending; the evidence lane may revise the initial plan.", "HOLD");
                    rows++;
                }
                foreach (var held in plan.UnreviewedHeldDeletions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMarkerRow(writer, "held-deletion", "unreviewed-held", held.Local, held.Path, held.Rule,
                        "Deletion needs reviewed authority.", "HOLD");
                    rows++;
                }
                foreach (var heldPath in unapproved ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMarkerRow(writer, "held-deletion", "unreviewed-held", null, heldPath, null,
                        "Deletion needs reviewed authority.", "HOLD");
                    rows++;
                }
                foreach (var pending in plan.RecoveryPending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMarkerRow(writer, "recovery-pending", "pending", null, pending, null,
                        "Recovery unavailable; deletions and replacements paused. Nothing is executed.", "HOLD");
                    rows++;
                }
                foreach (var notice in plan.Notices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMarkerRow(writer, "notice", "notice", null, notice.Path, notice.Rule.ToString(
                            CultureInfo.InvariantCulture),
                        notice.ReasonKey + ": " + notice.Detail);
                    rows++;
                }
                foreach (var restore in PendingRestorePreviewNotices(pair.PairId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMarkerRow(writer, "recovery-restore", "pending", null, restore.Path, null,
                        "Restore retry and verification are deferred in a read-only preview; the path remains protected. " + restore.Detail,
                        "RESTORE");
                    rows++;
                }
                foreach (var intent in unsupported)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteActionRow(writer, pair, intent, "unsupported-reconcile", "UNSUPPORTED-RECONCILE");
                    rows++;
                }
                // A sleeping retained intent is not necessarily named by a notice, so it is exported directly,
                // as a pending row, from the isolated store's unresolved set.
                var exportedIntents = unsupported.Select(intent => intent.ActionId).ToHashSet(StringComparer.Ordinal);
                foreach (var intent in _store.UnresolvedIntents(pair.PairId).Where(intent => !exportedIntents.Contains(intent.ActionId)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteActionRow(writer, pair, intent, "pending-intent");
                    rows++;
                }
            }
            File.Move(partial, path);
            return rows;
        }
        catch
        {
            try
            {
                if (created && File.Exists(partial)) File.Delete(partial);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private IEnumerable<Notice> PendingRestorePreviewNotices(string pairId)
    {
        string? after = null;
        while (true)
        {
            var page = _store.RecoveryRestoreNoticesPage(pairId, after, 256);
            foreach (var notice in page)
                if (notice.ConflictOperation?.Completed.Contains("ignored") != true)
                    yield return notice;
            if (page.Count < 256) yield break;
            after = page[^1].NoticeId;
        }
    }

    private static void WriteActionRow(TextWriter writer, PairDef pair, SyncAction action, string disposition,
        string? kindOverride = null) =>
        WriteCsvRow(writer,
            "action", action.ActionId, disposition, kindOverride ?? (action.Kind == ActionKind.Hold ? "HOLD" : action.Kind.ToString()),
            action.Target == Side.A ? "A" : "B", Direction(pair, action), action.Path, action.FromPath,
            action.Rule.ToString(CultureInfo.InvariantCulture), action.RecoveryFirst ? "true" : "false",
            action.Source?.ObjectId, action.Source?.VersionId, action.Source?.Hash,
            action.Source?.Size.ToString(CultureInfo.InvariantCulture),
            action.Source?.Mtime.ToString("O", CultureInfo.InvariantCulture),
            action.ExpectedTarget?.ObjectId, action.ExpectedTarget?.VersionId, action.ExpectedTarget?.Hash,
            action.ExpectedTarget?.Size.ToString(CultureInfo.InvariantCulture),
            action.ExpectedTarget?.Mtime.ToString("O", CultureInfo.InvariantCulture), kindOverride is null ? (action.Kind == ActionKind.Hold ? "Held by deletion identity or planning safety review; see the notice for this path." : null) :
                $"Original kind {action.Kind}; checkpoint {action.ProviderEffect?.Kind}. Reconciliation may require restoration or publication and is not supported read-only.");

    private static void WriteMarkerRow(TextWriter writer, string record, string disposition, Side? side,
        string? path, string? rule, string? detail, string? kind = null) =>
        WriteCsvRow(writer, record, null, disposition, kind, side is null ? null : side == Side.A ? "A" : "B",
            null, path, null, rule, null, null, null, null, null, null, null, null, null, null, null, detail);

    // The direction names the source and target endpoint roles, never a fixed side, so same-role pairs read
    // local-to-local or cloud-to-cloud. Adopt and Forget are metadata-only: neither endpoint is touched, and
    // Forget drops a baseline while leaving both sides unchanged.
    private static string Direction(PairDef pair, SyncAction action)
    {
        var target = SideRole(pair, action.Target);
        return action.Kind switch
        {
            ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth =>
                SideRole(pair, action.Target == Side.A ? Side.B : Side.A) + "-to-" + target,
            ActionKind.Adopt or ActionKind.Forget => "metadata-only",
            ActionKind.Delete => target + "-delete",
            ActionKind.Move => target + "-move",
            ActionKind.MkDir => target + "-mkdir",
            _ => target
        };
    }

    private static string SideRole(PairDef pair, Side side)
    {
        var root = side == Side.A ? pair.A : pair.B;
        return string.Equals(SyncProviders.Normalize(root.Provider), SyncProviders.Local, StringComparison.Ordinal)
            ? "local"
            : "cloud";
    }

    private static void WriteCsvRow(TextWriter writer, params string?[] fields)
    {
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0) writer.Write(',');
            writer.Write(CsvField(fields[index]));
        }
        writer.WriteLine();
    }

    // Compatibility entry point for existing reflection-based export fixtures.
    internal static string CsvField(string? value) => CsvFields.Encode(value);
}
