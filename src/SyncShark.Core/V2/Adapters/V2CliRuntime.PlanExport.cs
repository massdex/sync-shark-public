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
    private const int MaximumPlanPathCharacters = 32_768;
    // Set only by AcquireIsolatedPlanPreview, so a plan-only export can never run against a live or borrowed
    // runtime even if another caller reaches this method directly.
    private bool _planOnlyIsolated;

    // Every folder whose redirection could reach the live product folder, plus the files that carry state.
    private static readonly string[] IsolationFolders =
        ["state", "vNext", Path.Combine("vNext", "state"), "v2"];

    private static readonly string[] IsolationFiles =
    [
        Path.Combine("v2", "state.db"),
        Path.Combine("v2", "state.db-wal"),
        Path.Combine("v2", "state.db-shm"),
        Path.Combine("vNext", "state", "syncshark.db"),
        Path.Combine("vNext", "state", "syncshark.db-wal"),
        Path.Combine("vNext", "state", "syncshark.db-shm"),
        Path.Combine("state", "instance.lock")
    ];
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint OpenExisting = 3;

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
        await WaitForDataLockAsync(_runGate, progress, cancellationToken);
        try
        {
            var unsupported = _store.UnresolvedIntents(pairId).Where(PlanOnlyEndpoints.UnsupportedReconcile).ToArray();
            Plan plan;
            try
            {
                plan = await _run.PreviewIncrementalAsync(pairId, new RunProgressForwarder(progress),
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
            // A plan that could not be completed is never written as a complete export.
            if (plan.RunUnavailable is { } unavailable)
                throw new SyncRunPendingVerificationException(unavailable, 30);
            var rows = WritePlanCsv(export, pair, plan, unsupported, cancellationToken);
            progress("Each preview consumes its isolated copy. Stop the app and make a fresh copy of the database together with its -wal and -shm files before every preview.");
            progress($"Unsupported read-only reconciliation: {unsupported.Length}." +
                (unsupported.Length == 0 ? "" : " Export is not an exact execution plan; inspect UNSUPPORTED-RECONCILE rows."));
            return new SyncCliPlanExportData(pair.PairId, pair.Name, export, plan.Gen, plan.Actions.Count,
                plan.DeferredPlanningActions.Count, plan.SkippedActions.Count,
                plan.HeldDeletions.Count + plan.UnreviewedHeldDeletions.Count, plan.RecoveryPending.Count,
                plan.Notices.Count, _store.CountUnresolvedIntents(pairId), rows)
            { UnsupportedReconciles = unsupported.Length };
        }
        finally
        {
            _runGate.Release();
        }
    }

    // Opens the plan-only runtime against an explicit isolated folder. The checks run before the exclusive
    // lock and the store, so a refused data root never opens, creates or writes product state.
    private static V2CliRuntime AcquireIsolatedPlanPreview(SyncCliInvocation invocation)
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
                new EngineHostInputs(productRoot, instanceLock.StateAuthority, SharedHttp), V2Settings.Default,
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

    // An isolated data root is mandatory and must be its own canonical folder. The live product folder, any
    // alias (junction, symbolic link, 8.3 short name), either folder containing the other, and a state database
    // hardlinked to the live one are all refused before the runtime opens.
    internal static string ValidateIsolatedDataRoot(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Length > MaximumPlanPathCharacters ||
            !Path.IsPathFullyQualified(requested))
            throw new SyncCliUsageException(
                "run --plan-only requires an absolute --data-root folder; the live product folder is never defaulted.");
        if (!Directory.Exists(requested))
            throw new SyncCliUsageException(
                "--data-root must name an existing isolated product folder; it is never created or defaulted.");
        var full = TrimSeparators(Path.GetFullPath(requested));
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new SyncCliUsageException(
                "--data-root must not be a reparse point, junction or symbolic link. Use a real isolated folder.");
        string canonical;
        try
        {
            canonical = CanonicalDirectory(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SyncCliUsageException("--data-root could not be verified: " + error.Message);
        }
        if (!string.Equals(TrimSeparators(full), TrimSeparators(canonical), StringComparison.OrdinalIgnoreCase))
            throw new SyncCliUsageException("--data-root must be the canonical folder path; an alias such as a " +
                "junction or an 8.3 short name is not accepted.");
        var liveRoot = SafeCanonicalPath(SyncSharkRuntimePaths.ProductRoot);
        if (IsSameOrInside(canonical, liveRoot) || IsSameOrInside(liveRoot, canonical))
            throw new SyncCliUsageException("--data-root must be a folder separate from the live Sync Shark " +
                "product folder. Copy the product folder to an isolated location first.");
        RejectAliasedStateFolders(canonical);
        RejectSharedStateFiles(canonical, liveRoot);
        return canonical;
    }

    // A state subfolder that is a junction or symbolic link could silently redirect the lock, the v1 authority
    // database or the v2 database into the live product folder, so every state folder must resolve inside the
    // isolated root.
    private static void RejectAliasedStateFolders(string canonicalRoot)
    {
        foreach (var relative in IsolationFolders)
        {
            var path = Path.Combine(canonicalRoot, relative);
            if (!Directory.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new SyncCliUsageException("--data-root must not contain a redirected " + relative +
                    " folder. Copy the product folder instead of linking its subfolders.");
            string resolved;
            try
            {
                resolved = CanonicalDirectory(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new SyncCliUsageException("--data-root could not verify its " + relative + " folder: " +
                    error.Message);
            }
            if (!IsSameOrInside(resolved, canonicalRoot))
                throw new SyncCliUsageException("--data-root's " + relative + " folder resolves outside the " +
                    "isolated folder. Copy the product folder instead of linking its subfolders.");
        }
    }

    // The state files are what isolation protects: each existing isolated file must resolve inside the isolated
    // root (a symlinked database or sidecar outside is refused), and no existing file may share one identity
    // with the live product (a hardlink or alias is refused). An existing file whose identity cannot be read
    // fails closed rather than being assumed separate.
    private static void RejectSharedStateFiles(string isolatedRoot, string liveRoot)
    {
        foreach (var relative in IsolationFiles)
        {
            var isolatedPath = Path.Combine(isolatedRoot, relative);
            if (!File.Exists(isolatedPath)) continue;
            try
            {
                var canonical = CanonicalFile(isolatedPath);
                if (!IsSameOrInside(canonical, isolatedRoot))
                    throw new SyncCliUsageException("--data-root's " + relative + " resolves outside the " +
                        "isolated folder. Copy the product folder instead of linking its files.");
                var livePath = Path.Combine(liveRoot, relative);
                if (!File.Exists(livePath)) continue;
                if (FileIdentity(isolatedPath) == FileIdentity(livePath))
                    throw new SyncCliUsageException("--data-root shares the live product state file " + relative +
                        " (hardlink). Copy the product folder instead of linking its files.");
            }
            catch (SyncCliUsageException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new SyncCliUsageException("--data-root could not be verified against the live product " +
                    "state (" + relative + "): " + error.Message + " Close any running Sync Shark process and " +
                    "retry; the preview fails closed.");
            }
        }
    }

    private static PilotStateFileIdentity FileIdentity(string path)
    {
        using var handle = PilotDirectoryLease.CreateFile(path, FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("The state file identity could not be opened.");
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information))
            throw new IOException("The state file identity could not be read.");
        return new PilotStateFileIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static string CanonicalFile(string path)
    {
        using var handle = PilotDirectoryLease.CreateFile(path, FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The state file could not be opened for its canonical path.");
        return PilotDirectoryLease.FinalPath(handle);
    }

    private static string CanonicalDirectory(string path)
    {
        using var handle = PilotDirectoryLease.CreateFile(path, FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting,
            PilotDirectoryLease.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The folder could not be opened for its canonical path.");
        return PilotDirectoryLease.FinalPath(handle);
    }

    // Best-effort canonicalisation for a folder that may not exist (a live folder is normally present). A
    // resolution failure keeps the nominal full path, which still lets the containment checks run.
    private static string SafeCanonicalPath(string path)
    {
        var full = TrimSeparators(Path.GetFullPath(path));
        try
        {
            return Directory.Exists(full) ? TrimSeparators(CanonicalDirectory(full)) : full;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    // The export file must be absolute, sit in an existing folder, and be outside the live and isolated
    // product folders and both paired trees. No folder is created, and no paired tree is ever written.
    internal static string ValidatePlanExportPath(string exportPath, IReadOnlyList<PairDef> pairs, string isolatedRoot)
    {
        if (string.IsNullOrWhiteSpace(exportPath) || exportPath.Length > MaximumPlanPathCharacters ||
            !Path.IsPathFullyQualified(exportPath))
            throw new SyncCliUsageException(
                "--export requires an absolute CSV file path outside both folders of the pair.");
        var full = Path.GetFullPath(exportPath);
        var directory = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new SyncCliUsageException("--export must name a file in an existing folder; no folder is created.");
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new SyncCliUsageException("--export requires a valid file name.");
        // The export never replaces anything: a new file is required, so a planted file or link at the target
        // is refused instead of being overwritten.
        if (File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null)
            throw new SyncCliUsageException("--export already exists; choose a new CSV file. A plan-only export " +
                "never overwrites an existing file.");
        string canonical;
        try
        {
            canonical = Path.Combine(CanonicalDirectory(directory), name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SyncCliUsageException("--export could not be verified: " + error.Message);
        }
        if (IsSameOrInside(canonical, SafeCanonicalPath(isolatedRoot)) ||
            IsSameOrInside(canonical, SafeCanonicalPath(SyncSharkRuntimePaths.ProductRoot)))
            throw new SyncCliUsageException(
                "--export must be outside the live and isolated Sync Shark data folders.");
        foreach (var root in pairs.SelectMany(pair => new[] { pair.A, pair.B }))
        {
            if (string.IsNullOrWhiteSpace(root.RootPath) || !Path.IsPathFullyQualified(root.RootPath)) continue;
            if (IsSameOrInside(canonical, SafeCanonicalPath(root.RootPath)))
                throw new SyncCliUsageException(
                    "--export must be outside both folders of the pair; choose a file outside the paired trees.");
        }
        RejectDropboxExport(canonical);
        return canonical;
    }

    private static void RejectDropboxExport(string path)
    {
        for (var folder = Directory.GetParent(path); folder is not null; folder = folder.Parent)
            if (folder.Name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) ||
                folder.Name.StartsWith("Dropbox (", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(folder.FullName, ".dropbox")))
                throw new SyncCliUsageException("--export must be outside Dropbox folders.");
        foreach (var location in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var info = Path.Combine(Environment.GetFolderPath(location), "Dropbox", "info.json");
            if (!File.Exists(info)) continue;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
                foreach (var account in document.RootElement.EnumerateObject())
                    if (account.Value.TryGetProperty("path", out var root) && root.GetString() is { } desktop &&
                        IsSameOrInside(path, SafeCanonicalPath(desktop)))
                        throw new SyncCliUsageException("--export must be outside Dropbox folders.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { throw new SyncCliUsageException("Dropbox folder locations could not be verified: " + error.Message); }
        }
    }

    // An isolated data folder must not sit inside a paired tree, where the engine's own state files would be
    // scanned as pair content. Both sides are checked; a remote path is not a local folder and is skipped.
    private void RejectDataRootInsidePair(PairDef pair)
    {
        var isolated = SafeCanonicalPath(_productRoot);
        foreach (var endpoint in new[] { pair.A, pair.B })
        {
            if (string.IsNullOrWhiteSpace(endpoint.RootPath) ||
                !Path.IsPathFullyQualified(endpoint.RootPath)) continue;
            if (IsSameOrInside(isolated, SafeCanonicalPath(endpoint.RootPath)))
                throw new SyncCliUsageException("--data-root must be outside both folders of the pair; choose an " +
                    "isolated copy outside the paired trees.");
        }
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrInside(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var candidate = TrimSeparators(path);
        var container = TrimSeparators(root);
        if (string.Equals(candidate, container, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // One flat CSV table: every planned action, then the plan's deferred, skipped, held and pending markers, so a
    // reader sees exactly what was planned and what was deliberately not planned. The write is all-or-nothing: a
    // uniquely named sibling partial is created new and moved into place only after every row was written.
    private int WritePlanCsv(string path, PairDef pair, Plan plan, IReadOnlyList<SyncAction> unsupported,
        CancellationToken cancellationToken)
    {
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

    internal static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
