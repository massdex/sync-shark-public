using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// SS-D14 sparse planning context proof: one immutable per-baseline classification per planning
// invocation, a reviewed selection that bounds the lexical and decision work, and the complete global
// context retained around it. Every case below drives the real SyncAuthorityReconciliationPlanner over a
// real isolated SQLite authority and real observations produced by SyncAuthorityObservationCoordinator.
//
// The fixture supplies local files, scripted remote listing entries and advertised identities only. No
// fixture replaces the decision under proof: no live account, credential, network, installed product,
// schema or provider effect is reachable from this file. Counters are observations of production work;
// they never choose a decision, an identity, an ordering or an effect.
internal static class SparsePlanningContextChecks
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string linkPath, string existingPath, IntPtr securityAttributes);

    private const string InstallationId = "installation-sparse-planning-context";
    private const string ProfileId = "sparse-planning-context-profile";
    private const string SourceRevision = "sparse-planning-context-1";
    private const string LocalRootId = "sparse-local-root";
    private const string RemoteRootId = "sparse-cloud-root";
    private const string SecondaryLocalRootId = "sparse-local-root-partner";
    private const string SecondaryRemoteRootId = "sparse-cloud-root-partner";
    private const int ScaleUnrelatedRows = 100_000;
    private const int ScaleSelectedFirstRun = 10;
    private const int ScaleSelectedSecondRun = 110;
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static readonly List<Dictionary<string, object?>> Measurements = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root, bool correctionsOnly = false)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated sparse planning context fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        Measurements.Clear();
        _assertions = 0;
        SyncAuthorityPlanningContextObservation.CompletedForTest = null;

        if (!correctionsOnly) await CaseAsync("A-classification-once-and-sparse-lexicals",
            "One immutable classification per saved baseline, and lexical plus decision work bounded by the reviewed selection rather than the complete universe",
            ClassificationOnceSparseSelectionAsync);
        if (!correctionsOnly) await CaseAsync("B-membership-alias-and-coordination",
            "Multiple local memberships of one object, a coordinated reservation and a participant pair keep the complete global context while an independent eligible file is still planned",
            MembershipAndCoordinationAsync);
        if (!correctionsOnly) await CaseAsync("C-sparse-equals-full-context",
            "Null, complete, empty and absent-path selections preserve the full-context result order and counts, including different-case and Unicode names and a baseline retained on one endpoint only",
            SparseEqualsFullContextAsync);
        await CaseAsync("D-reviewed-batch-boundaries",
            "The reviewed concurrency path decides each selected file exactly once and reproduces the sequential result",
            ReviewedBatchBoundariesAsync);
        if (!correctionsOnly) await CaseAsync("E-sparse-scale",
            "100000 unrelated observed rows with 10 and 110 selected paths: classification, sorting and decision work follow the selection while context loading stays proportional to the total",
            SparseScaleAsync);
        await CaseAsync("F-cancellation-and-durable-safety",
            "Cancellation during preparation decides nothing, negative reviewed kinds plan no effect, unchanged outcomes stay nondurable and terminal progress remains monotonic",
            CancellationAndDurableSafetyAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "SparsePlanningContextChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["scope"] = correctionsOnly ? "D and F only; other cases not rerun" : "all six cases",
            ["assertions"] = _assertions,
            ["measurements"] = Measurements,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "sparse-planning-context-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var failures = Cases.Count(item => Equals(item["result"], "FAIL"));
        if (failures > 0) throw new InvalidOperationException($"Sparse planning context: {failures} independent cases failed; complete results retained.");
        Console.WriteLine("PASS sparse planning context: " + _assertions + " assertions; one immutable" +
            " baseline classification per invocation, selection-bounded lexical and decision work with the" +
            " complete global context retained, and no effect, durable record or progress claim for skipped or" +
            " cancelled work.");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.ToString()
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("Sparse planning context assertion failed: " + label);
    }

    private static void AssertEquals<T>(T expected, T actual, string label) =>
        Assert(EqualityComparer<T>.Default.Equals(expected, actual),
            label + " (expected " + expected + ", observed " + actual + ")");

    private sealed record PlanRun(IReadOnlyList<SyncAuthorityPlannerOutcome> Outcomes,
        SyncAuthorityPlanningWorkCounters Counters, long ElapsedMilliseconds, long AllocatedBytes,
        IReadOnlyList<SyncComparisonProgress> Progress);

    /// <summary>The scripted remote listing of one fixture pair. It supplies listing entries only.</summary>
    public class ScriptedRemote : DispatchProxy
    {
        internal IReadOnlyList<PilotRemoteEntry> Entries { get; set; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Task.CompletedTask,
            nameof(ISyncRemoteStore.ListEntriesAsync) => Task.FromResult(Entries),
            _ => throw new InvalidOperationException(
                "Unexpected provider effect in the sparse planning context fixture: " + method?.Name)
        };
    }

    /// <summary>
    /// One real isolated authority, one local/cloud pair and the real observation coordinator. Local
    /// files are real files under the fixture root; the remote side is a scripted listing.
    /// </summary>
    private sealed class Fixture
    {
        private readonly Dictionary<string, PilotRemoteEntry> _remote = new(StringComparer.OrdinalIgnoreCase);

        private Fixture(SyncAuthorityPlannerOptions options) => Options = options;

        internal SyncPair Pair { get; private init; } = null!;

        internal SyncAuthorityDatabase Db { get; private init; } = null!;
        internal SyncAuthorityCommandAdmission Admission { get; private init; } = null!;

        internal string Local { get; private init; } = "";

        internal SyncAuthorityPlannerOptions Options { get; }

        internal static async Task<Fixture> CreateAsync(string root, string id,
            SyncAuthorityPlannerOptions? options = null)
        {
            var caseRoot = Path.Combine(Path.GetFullPath(root), id);
            var local = Path.Combine(caseRoot, "local");
            Directory.CreateDirectory(local);
            PilotDirectoryIdentity identity;
            using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
            var pair = new SyncPair("sparse-" + id, "Sparse planning context " + id, local,
                identity.VolumeSerialNumber, identity.FileId, "/Remote", "id:remote-root",
                Provider: SyncProviders.Dropbox);
            var db = new SyncAuthorityDatabase(Path.Combine(caseRoot, "authority.db"));
            await db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await db.RegisterPairAsync(pair.Id, 1);
            await db.RegisterRootBindingAsync(LocalRootId, pair.Id, "local", 1,
                JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), local);
            await db.RegisterRootBindingAsync(RemoteRootId, pair.Id, "dropbox", 1,
                """{"folderId":"id:remote-root"}""", pair.DropboxRoot);
            var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            return new Fixture(options ?? DefaultOptions) { Pair = pair, Db = db, Local = local, Admission = admission };
        }

        internal static SyncAuthorityPlannerOptions DefaultOptions { get; } = new(1, ProfileId,
            "sparse-control-local", "sparse-control-cloud", EnableDeletionEffects: true,
            EnableMoveEffects: true, DeletionMinimumInterval: TimeSpan.Zero);

        internal async Task WriteLocalAsync(string relativePath, string content)
        {
            var full = Path.Combine(Local, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        internal string LocalFullPath(string relativePath) =>
            Path.Combine(Local, relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void ScriptFile(string relativePath, string content) =>
            _remote[relativePath] = new PilotRemoteEntry(relativePath, PilotEntryKind.File, "id:" + relativePath,
                "rev:" + relativePath, Encoding.UTF8.GetByteCount(content), DropboxContent(content));

        internal async Task ObserveAsync()
        {
            // The production Host keeps the same guarded connection-reuse scope for its observation
            // phase. Exercise that calling contract; each metadata batch still commits independently.
            await using var connections = Db.BeginConnectionReuseScope();
            var remote = DispatchProxy.Create<ISyncRemoteStore, ScriptedRemote>();
            ((ScriptedRemote)(object)remote).Entries = _remote.Values.ToArray();
            var observer = new SyncAuthorityObservationCoordinator(Pair, remote, Db, LocalRootId, RemoteRootId);
            await observer.ObserveAsync("initial");
            await observer.ObserveAsync("initial");
        }

        internal async Task<IReadOnlyList<SyncDecision>> AdoptAsync() =>
            await new SyncAuthorityInitialPlanner(Db).AdoptAsync(Pair.Id);

        internal async Task<SyncAuthorityPairObservationSet> LoadObservationSetAsync() =>
            await Db.LoadPlanningObservationSetAsync(Pair.Id, Options.ConfigurationRevision, LocalRootId,
                RemoteRootId, await Db.LoadLatestCompleteScanIdAsync(Pair.Id, "local"),
                await Db.LoadLatestCompleteScanIdAsync(Pair.Id, "dropbox"));

        internal async Task<PlanRun> PlanAsync(IReadOnlySet<string>? filter,
            IReadOnlyDictionary<string, SyncAuthorityOperationKind>? expectedKinds = null,
            string? operationGroupId = null, int concurrency = 1, IReadOnlySet<string>? coordinated = null,
            IReadOnlyList<SyncAuthorityPairObservationSet>? participants = null,
            CancellationToken cancellationToken = default, SyncAuthorityPairObservationSet? suppliedInput = null)
        {
            await using var connections = Db.BeginConnectionReuseScope();
            var observationSet = suppliedInput ?? await LoadObservationSetAsync();
            if (operationGroupId is not null)
                await Db.RegisterOperationGroupAsync(operationGroupId, "compound", "{}", cancellationToken);
            var progress = new List<SyncComparisonProgress>();
            SyncAuthorityPlanningWorkCounters? captured = null;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            IReadOnlyList<SyncAuthorityPlannerOutcome> outcomes;
            SyncAuthorityPlanningContextObservation.CompletedForTest = counters => captured = counters;
            try
            {
                var planner = new SyncAuthorityReconciliationPlanner(Db, Options, Admission, filter, operationGroupId,
                    null, null, expectedKinds, step =>
                    {
                        lock (progress) progress.Add(step);
                        return Task.CompletedTask;
                    }, null, null, null, concurrency);
                outcomes = await planner.PlanAsync(observationSet,
                    SyncAuthorityReconciliationPlanner.ObservationSetIdentity(observationSet), coordinated,
                    participants, cancellationToken);
            }
            finally
            {
                watch.Stop();
                SyncAuthorityPlanningContextObservation.CompletedForTest = null;
            }
            return new PlanRun(outcomes, captured ?? new SyncAuthorityPlanningWorkCounters(),
                watch.ElapsedMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore, progress);
        }

        internal async Task<int> RunnableOperationsAsync() =>
            (await Db.ListRunnableOperationsForPairAsync(Pair.Id)).Count;
    }

    private static string DropboxContent(string text) =>
        SyncContentIdentity.Compute(SyncProviders.Dropbox, Encoding.UTF8.GetBytes(text));

    private static HashSet<string> Select(params string[] paths) =>
        new(paths, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The complete comparison universe of one observation set. The reviewed complete selection must
    /// reproduce exactly the null-filter result; anything the sparse run drops or fabricates shows up as
    /// a difference rather than being normalized away.
    /// </summary>
    private static HashSet<string> CompleteSelection(SyncAuthorityPairObservationSet observationSet)
    {
        var selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        selection.UnionWith(observationSet.Local.Select(item => item.RelativePath));
        selection.UnionWith(observationSet.Dropbox.Select(item => item.RelativePath));
        selection.UnionWith(observationSet.SynchronizationPoints
            .Select(item => item.LocalRelativePath ?? item.DropboxRelativePath)
            .Where(path => path is not null).Select(path => path!));
        selection.UnionWith(observationSet.LocalUncertainPaths);
        selection.UnionWith(observationSet.DropboxUncertainPaths);
        return selection;
    }

    /// <summary>The outcome identity every sparse/full comparison binds: kind, path, object and effect ids.</summary>
    private static string[] Describe(IReadOnlyList<SyncAuthorityPlannerOutcome> outcomes) =>
        outcomes.Select(item => string.Join("|", item.Kind.ToString(), item.RelativePath, item.ObjectId,
            item.OperationId ?? "-", item.ConflictId ?? "-", item.TombstoneId ?? "-")).ToArray();

    private static async Task<int> CountAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static Task<int> CountDecisionsAsync(string databasePath) =>
        CountAsync(databasePath, "SELECT COUNT(*) FROM reconciliation_decision;");

    private static Task<int> CountRoutineDecisionsAsync(string databasePath) => CountAsync(databasePath,
        "SELECT COUNT(*) FROM reconciliation_decision WHERE decision_kind IN ('unchanged','skipped');");

    // ----- A: one classification per baseline, selection-bounded lexical and decision work -----------

    private static async Task ClassificationOnceSparseSelectionAsync()
    {
        var fixture = await Fixture.CreateAsync(_root, "a-sparse-selection");
        const int saved = 40;
        const int arrivals = 60;
        var savedPaths = new List<string>();
        for (var index = 0; index < saved; index++)
        {
            var path = "saved-item-" + index.ToString("D2", CultureInfo.InvariantCulture) + ".bin";
            var content = "saved payload " + index.ToString(CultureInfo.InvariantCulture);
            await fixture.WriteLocalAsync(path, content);
            fixture.ScriptFile(path, content);
            savedPaths.Add(path);
        }
        var arrivalPaths = new List<string>();
        for (var index = 0; index < arrivals; index++)
        {
            var path = "arrival-row-" + index.ToString("D2", CultureInfo.InvariantCulture) + ".bin";
            fixture.ScriptFile(path, "arrival payload " + index.ToString(CultureInfo.InvariantCulture));
            arrivalPaths.Add(path);
        }
        await fixture.ObserveAsync();
        await fixture.AdoptAsync();
        var observationSet = await fixture.LoadObservationSetAsync();
        AssertEquals(saved, observationSet.SynchronizationPoints.Count,
            "every matching saved path holds one synchronization baseline");
        AssertEquals(saved, observationSet.Local.Count, "the local inventory holds exactly the saved files");
        AssertEquals(saved + arrivals, observationSet.Dropbox.Count,
            "the remote inventory holds the saved and the unrelated arrival rows");

        var selection = Select(savedPaths[0], savedPaths[1], savedPaths[2], savedPaths[3]);
        var first = await fixture.PlanAsync(selection);
        var counters = first.Counters;
        AssertEquals(saved, counters.BaselineClassifications, "one classification per saved baseline");
        AssertEquals(saved, counters.LocalMembershipLookups, "one local membership lookup per saved baseline");
        AssertEquals(saved, counters.RemoteMembershipLookups, "one remote membership lookup per saved baseline");
        AssertEquals(saved, counters.ExclusionClassifications, "one exclusion classification per saved baseline");
        AssertEquals(saved, counters.CoordinationClassifications,
            "one coordination classification per saved baseline");
        AssertEquals(saved, counters.UncertaintyClassifications, "one uncertainty classification per saved baseline");
        AssertEquals(saved, counters.RelocationClassifications, "one relocation classification per saved baseline");
        AssertEquals(saved, counters.IdentityMoveClassifications, "one identity-move classification per baseline");
        AssertEquals(saved, counters.FilterClassifications, "one reviewed filter classification per saved baseline");
        AssertEquals(saved, counters.RelocatedBoundaryReuses,
            "the batch dependency boundary reuses each classified descriptor without a second index scan");
        AssertEquals(saved, counters.BaselineSortEntries, "the decision order sorts each classified descriptor once");
        AssertEquals(saved, counters.EligibilitySkips + counters.EligibleDecisionEntries,
            "every saved baseline is either skipped or decided exactly once");
        AssertEquals(selection.Count, counters.EligibleDecisionEntries,
            "only the selected saved baselines enter the decision loop");
        AssertEquals(saved + arrivals, counters.ComparisonUniversePaths,
            "the complete comparison universe is retained, not reduced to the selection");
        AssertEquals(selection.Count, counters.SelectedComparisonPaths,
            "only the selected universe paths are decided");
        AssertEquals(selection.Count, counters.LexicalSortEntries,
            "the lexical sort is bounded by the selection, never by the universe");
        AssertEquals(saved + arrivals - selection.Count, counters.UnselectedComparisonPaths,
            "the unselected universe remainder is still measured as context");
        AssertEquals(saved + arrivals - selection.Count, counters.UnselectedProgressAdvances,
            "the unselected remainder advances the full-context progress once, without a per-skip callback");
        AssertEquals(saved + arrivals, counters.FilterMembershipTests,
            "the reviewed filter is tested exactly once per complete universe path");
        AssertEquals(selection.Count, counters.SelectedPathsVisited, "every selected path is visited once");
        AssertEquals(selection.Count, counters.ComparisonPathsDecided + counters.SelectedPathsSkipped,
            "every visited selected path is either decided or skipped exactly once");
        Assert(first.Outcomes.Count == selection.Count &&
               first.Outcomes.All(item => selection.Contains(item.RelativePath)) &&
               first.Outcomes.All(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged),
            "the sparse pass returns one unchanged outcome per selected saved baseline and nothing else");
        AssertEquals(0, (await fixture.Db.LoadOpenConflictIdsAsync(fixture.Pair.Id)).Count,
            "an unchanged sparse pass claims no conflict");
        AssertEquals(0, await fixture.RunnableOperationsAsync(),
            "an unchanged sparse pass plans no operation");

        // Re-running the exact same invocation classifies exactly as much again: the classification is
        // once per invocation, is never accumulated across invocations and is never re-derived per use.
        var repeated = await fixture.PlanAsync(selection);
        AssertEquals(counters.BaselineClassifications, repeated.Counters.BaselineClassifications,
            "a repeated invocation classifies each saved baseline once");
        AssertEquals(counters.LocalMembershipLookups, repeated.Counters.LocalMembershipLookups,
            "a repeated invocation resolves each local membership lookup once");
        AssertEquals(counters.RemoteMembershipLookups, repeated.Counters.RemoteMembershipLookups,
            "a repeated invocation resolves each remote membership lookup once");
        AssertEquals(counters.BaselineSortEntries, repeated.Counters.BaselineSortEntries,
            "a repeated invocation sorts each classified descriptor once");
        AssertEquals(counters.LexicalSortEntries, repeated.Counters.LexicalSortEntries,
            "a repeated invocation sorts only the selected universe");
        Assert(Describe(first.Outcomes).SequenceEqual(Describe(repeated.Outcomes)),
            "a repeated sparse pass keeps the exact outcome identities");

        // Positive eligible work: the reviewed arrivals are planned once each with their exact members.
        var positiveSelection = Select(savedPaths[4], arrivalPaths[0], arrivalPaths[1]);
        var positive = await fixture.PlanAsync(positiveSelection);
        var planned = positive.Outcomes.Where(item =>
            item.Kind == SyncAuthorityPlannerOutcomeKind.Operation).ToArray();
        AssertEquals(2, planned.Length, "two independent reviewed arrivals are planned");
        AssertEquals(2, planned.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count(),
            "each reviewed arrival keeps its own immutable operation identity");
        Assert(planned.All(item => item.OperationId is not null && positiveSelection.Contains(item.RelativePath)),
            "every planned operation belongs to exactly one selected path");
        AssertEquals(1, positive.Outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged),
            "the selected unchanged baseline keeps its no-effect outcome");
        var operation = await fixture.Db.LoadOperationAsync(planned[0].OperationId!);
        AssertEquals(SyncAuthorityOperationKind.CreateLocal, operation.Kind,
            "a remote-only reviewed arrival plans a local create");
        AssertEquals(2, operation.Members.Count, "the planned create binds one source and one destination member");
        Assert(operation.Members.Any(item => item.Role == "source" && item.RootBindingId == RemoteRootId &&
                item.ObjectId == planned[0].ObjectId && item.ExpectedPathKey == planned[0].RelativePath),
            "the source member binds the exact remote object and path");
        Assert(operation.Members.Any(item => item.Role == "destination" && item.RootBindingId == LocalRootId &&
                item.ObjectId == planned[0].ObjectId && item.ExpectedPathKey == planned[0].RelativePath),
            "the destination member binds the exact local object and path");
        AssertEquals(2, await fixture.RunnableOperationsAsync(),
            "exactly the two reviewed arrivals hold runnable operations");
        Measurements.Add(new Dictionary<string, object?>
        {
            ["case"] = "A-measured",
            ["saved_baselines"] = saved,
            ["universe_paths"] = saved + arrivals,
            ["selected_paths"] = selection.Count,
            ["baseline_classifications"] = counters.BaselineClassifications,
            ["eligible_decisions"] = counters.EligibleDecisionEntries,
            ["lexical_sort_entries"] = counters.LexicalSortEntries,
            ["elapsed_ms"] = first.ElapsedMilliseconds,
            ["allocated_bytes"] = first.AllocatedBytes
        });
    }

    // ----- B: multiple memberships, coordinated reservations and participant context ------------------

    private static async Task MembershipAndCoordinationAsync()
    {
        var fixture = await Fixture.CreateAsync(_root, "b-membership-alias");
        const string baselinePath = "shared.bin";
        const string aliasPath = "shared-alias.bin";
        const string independentPath = "independent-arrival.bin";
        const string content = "shared payload";
        await fixture.WriteLocalAsync(baselinePath, content);
        fixture.ScriptFile(baselinePath, content);
        fixture.ScriptFile(independentPath, "independent payload");
        await fixture.ObserveAsync();
        await fixture.AdoptAsync();
        var adopted = await fixture.LoadObservationSetAsync();
        AssertEquals(1, adopted.SynchronizationPoints.Count, "one saved baseline was adopted");
        var objectId = adopted.SynchronizationPoints[0].ObjectId;

        // The production scanner rejects simultaneous hardlink aliases before publishing a cut.
        // Preserve that boundary and prove that the failed observation never creates a runnable effect.
        if (!CreateHardLinkW(fixture.LocalFullPath(aliasPath), fixture.LocalFullPath(baselinePath), IntPtr.Zero))
            throw new IOException("The isolated hard-link fixture could not be created.",
                System.Runtime.InteropServices.Marshal.GetExceptionForHR(System.Runtime.InteropServices.Marshal.GetHRForLastWin32Error()));
        var aliasRejected = false;
        try { await fixture.ObserveAsync(); }
        catch (InvalidDataException error) when (error.Message.Contains("same retained object", StringComparison.Ordinal))
        { aliasRejected = true; }
        Assert(aliasRejected, "the real scanner rejects duplicate physical identity before planning");
        AssertEquals(0, await fixture.RunnableOperationsAsync(), "the refused alias observation creates no runnable effect");
        File.Delete(fixture.LocalFullPath(aliasPath));
        await fixture.ObserveAsync();
        var observationSet = await fixture.LoadObservationSetAsync();
        var retained = observationSet.Local.Single(item => item.ObjectId == objectId);
        // Separately exercise the actual planner's defensive ambiguous-input decision. This is explicitly
        // constructed input, never claimed to be a successfully published scanner observation.
        var ambiguousInput = observationSet with
        {
            Local = observationSet.Local.Append(retained with
            { RelativePath = aliasPath, MembershipId = retained.MembershipId + "-adversarial-alias" }).ToArray()
        };
        var memberships = ambiguousInput.Local.Where(item => item.ObjectId == objectId).ToArray();
        AssertEquals(2, memberships.Length, "the constructed adversarial planner input contains both memberships");
        var aliased = await fixture.PlanAsync(Select(baselinePath), suppliedInput: ambiguousInput);
        AssertEquals(1, aliased.Outcomes.Count, "the aliased object still produces exactly one decision");
        AssertEquals(SyncAuthorityPlannerOutcomeKind.Blocked, aliased.Outcomes[0].Kind,
            "an ambiguous multi-membership identity plans no effect");
        Assert(aliased.Outcomes[0].Reason.Contains("multiple or ambiguous", StringComparison.Ordinal),
            "the refusal keeps its retained ambiguous-identity reason");
        Assert(memberships.Any(item => item.RelativePath == aliased.Outcomes[0].RelativePath),
            "the refusal names one of the exact retained membership paths");
        AssertEquals(0, await fixture.RunnableOperationsAsync(),
            "an ambiguous identity holds no runnable operation");

        // A coordinated reservation still suppresses the same object before any decision work.
        var coordinated = await fixture.PlanAsync(Select(baselinePath), coordinated: Select(objectId));
        AssertEquals(0, coordinated.Outcomes.Count, "a coordinated reservation suppresses the decision");
        AssertEquals(1, coordinated.Counters.EligibilitySkips,
            "the coordinated baseline is skipped as an eligibility-only skip");
        AssertEquals(0, coordinated.Counters.EligibleDecisionEntries,
            "the coordinated baseline never enters the decision loop");

        // The complete participant context is retained and never changes the sparse decision itself.
        var partner = await AddPartnerPairAsync(fixture);
        var participantSet = await fixture.LoadObservationSetAsync();
        var withParticipant = await fixture.PlanAsync(Select(baselinePath),
            participants: [participantSet, partner]);
        var withoutParticipant = await fixture.PlanAsync(Select(baselinePath));
        Assert(Describe(withParticipant.Outcomes).SequenceEqual(Describe(withoutParticipant.Outcomes)),
            "a participant pair keeps the decision identity, path, object and reason identical");
        AssertEquals(observationSet.SynchronizationPoints.Count,
            withParticipant.Counters.BaselineClassifications,
            "a participant pair never changes how many baselines this pair classifies");

        // An independent eligible file in the same sparse selection is still planned.
        var positive = await fixture.PlanAsync(Select(baselinePath, independentPath));
        AssertEquals(1, positive.Outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Operation),
            "the independent eligible arrival is planned");
        AssertEquals(1, positive.Outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged),
            "the real published baseline remains unchanged beside the independent eligible arrival");

        // A reviewed deletion of one endpoint still binds the participant planning authorities.
        var deletion = await Fixture.CreateAsync(_root, "b-participants");
        await deletion.WriteLocalAsync("gone.bin", "gone payload");
        deletion.ScriptFile("gone.bin", "gone payload");
        await deletion.ObserveAsync();
        await deletion.AdoptAsync();
        File.Delete(deletion.LocalFullPath("gone.bin"));
        await deletion.ObserveAsync();
        var deletionSet = await deletion.LoadObservationSetAsync();
        AssertEquals(1, deletionSet.SynchronizationPoints.Count, "the deleted path keeps its saved baseline");
        var deletionPartner = await AddPartnerPairAsync(deletion);
        var deleted = await deletion.PlanAsync(Select("gone.bin"),
            participants: [deletionSet, deletionPartner]);
        var deleteOutcome = deleted.Outcomes.SingleOrDefault(item => item.OperationId is not null);
        Assert(deleteOutcome is not null,
            "the absent local membership plans the reviewed deletion rather than an unauthorized effect");
        var deleteOperation = await deletion.Db.LoadOperationAsync(deleteOutcome!.OperationId!);
        AssertEquals(SyncAuthorityOperationKind.DeleteRemote, deleteOperation.Kind,
            "the absent local membership plans a remote deletion");
        AssertEquals(2, deleteOperation.PlanningAuthorities.Select(item => item.PairId)
                .Distinct(StringComparer.Ordinal).Count(),
            "a reviewed deletion binds the primary and the participant planning authorities");
    }

    private static async Task<SyncAuthorityPairObservationSet> AddPartnerPairAsync(Fixture fixture)
    {
        var partnerLocal = Path.Combine(Path.GetFullPath(_root), "partner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(partnerLocal);
        await File.WriteAllTextAsync(Path.Combine(partnerLocal, "partner-local.bin"), "partner local payload");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(partnerLocal)) identity = lease.Identity;
        var pair = new SyncPair("sparse-partner", "Sparse planning context partner", partnerLocal,
            identity.VolumeSerialNumber, identity.FileId, "/RemotePartner", "id:remote-root-partner",
            Provider: SyncProviders.Dropbox);
        await fixture.Db.RegisterPairAsync(pair.Id, 1);
        await fixture.Db.RegisterRootBindingAsync(SecondaryLocalRootId, pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), partnerLocal);
        await fixture.Db.RegisterRootBindingAsync(SecondaryRemoteRootId, pair.Id, "dropbox", 1,
            """{"folderId":"id:remote-root-partner"}""", pair.DropboxRoot);
        var remote = DispatchProxy.Create<ISyncRemoteStore, ScriptedRemote>();
        ((ScriptedRemote)(object)remote).Entries =
        [
            new PilotRemoteEntry("partner-file.bin", PilotEntryKind.File, "id:partner-file", "rev:partner-file",
                Encoding.UTF8.GetByteCount("partner payload"), DropboxContent("partner payload"))
        ];
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, fixture.Db, SecondaryLocalRootId,
            SecondaryRemoteRootId);
        await observer.ObserveAsync("initial");
        await observer.ObserveAsync("initial");
        return await fixture.Db.LoadPlanningObservationSetAsync(pair.Id, fixture.Options.ConfigurationRevision,
            SecondaryLocalRootId, SecondaryRemoteRootId,
            await fixture.Db.LoadLatestCompleteScanIdAsync(pair.Id, "local"),
            await fixture.Db.LoadLatestCompleteScanIdAsync(pair.Id, "dropbox"));
    }

    // ----- C: a sparse reviewed selection reproduces the complete-context result ---------------------

    private static async Task SparseEqualsFullContextAsync()
    {
        var options = new SyncAuthorityPlannerOptions(1, ProfileId, "sparse-control-local",
            "sparse-control-cloud", EnableDeletionEffects: false, EnableMoveEffects: true,
            DeletionMinimumInterval: TimeSpan.Zero);
        var fixture = await Fixture.CreateAsync(_root, "c-sparse-equals-full", options);
        const string cased = "Case-File.TXT";
        const string unicode = "Ünïcode-文件-Ünïcode.txt";
        const string oneSidedPath = "One-Sided.bin";
        await fixture.WriteLocalAsync(cased, "case payload");
        fixture.ScriptFile(cased, "case payload");
        await fixture.WriteLocalAsync(unicode, "unicode payload");
        fixture.ScriptFile(unicode, "unicode payload");
        await fixture.WriteLocalAsync(oneSidedPath, "one sided payload");
        fixture.ScriptFile(oneSidedPath, "one sided payload");
        await fixture.ObserveAsync();
        await fixture.AdoptAsync();
        AssertEquals(3, (await fixture.LoadObservationSetAsync()).SynchronizationPoints.Count,
            "three saved baselines were adopted before the one-endpoint change");
        File.Delete(fixture.LocalFullPath(oneSidedPath));
        await fixture.ObserveAsync();
        var observationSet = await fixture.LoadObservationSetAsync();
        var complete = CompleteSelection(observationSet);
        AssertEquals(3, complete.Count, "the complete universe holds the two-sided and one-sided paths");

        var full = await fixture.PlanAsync(null);
        var sparse = await fixture.PlanAsync(complete);
        Assert(Describe(full.Outcomes).SequenceEqual(Describe(sparse.Outcomes)),
            "the complete reviewed selection reproduces the full-context result order and identities");
        AssertEquals(full.Outcomes.Count, sparse.Outcomes.Count,
            "the sparse result keeps the full-context outcome count");
        AssertEquals(observationSet.SynchronizationPoints.Count, sparse.Counters.EligibleDecisionEntries,
            "each saved baseline of a complete selection is decided exactly once");
        AssertEquals(complete.Count, sparse.Counters.SelectedComparisonPaths,
            "the complete reviewed selection covers the whole universe");
        AssertEquals(0, sparse.Counters.UnselectedComparisonPaths,
            "a complete reviewed selection leaves no universe path unselected");
        AssertEquals(complete.Count, full.Counters.LexicalSortEntries,
            "the full-context pass sorts exactly the complete universe");
        AssertEquals(complete.Count, sparse.Counters.LexicalSortEntries,
            "the complete reviewed selection sorts exactly the complete universe");
        Assert(full.Outcomes.Any(item => item.RelativePath == unicode) &&
               full.Outcomes.Any(item => item.RelativePath == cased),
            "the different-case name and the Unicode name are both decided exactly");
        Assert(full.Outcomes.All(item => complete.Contains(item.RelativePath)),
            "no outcome names a path outside the complete universe");

        var decisionsBefore = await CountDecisionsAsync(fixture.Db.DatabasePath);
        var empty = await fixture.PlanAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        AssertEquals(0, empty.Outcomes.Count, "an empty reviewed selection decides nothing");
        AssertEquals(0, empty.Counters.SelectedComparisonPaths,
            "an empty reviewed selection selects no universe path");
        AssertEquals(complete.Count, empty.Counters.ComparisonUniversePaths,
            "an empty reviewed selection keeps the complete universe");
        AssertEquals(complete.Count, empty.Counters.UnselectedProgressAdvances,
            "the unselected remainder is accounted once without a per-skip callback");
        AssertEquals(0, empty.Counters.ComparisonPathsDecided,
            "an empty reviewed selection decides no path");
        AssertEquals(0, empty.Counters.BatchDrains, "an empty reviewed selection drains no batch");
        AssertEquals(observationSet.SynchronizationPoints.Count, empty.Counters.BaselineClassifications,
            "an empty reviewed selection still classifies every saved baseline once");
        AssertEquals(decisionsBefore, await CountDecisionsAsync(fixture.Db.DatabasePath),
            "an empty reviewed selection writes no durable decision");
        AssertEquals(empty.Progress[^1].TotalChecks, empty.Progress[^1].CompletedChecks,
            "the terminal report still accounts for the complete context");

        var absent = await fixture.PlanAsync(Select("absent/never-observed.bin"));
        AssertEquals(0, absent.Outcomes.Count, "a selected path absent from the universe decides nothing");
        AssertEquals(0, absent.Counters.SelectedComparisonPaths,
            "a selected path absent from the universe is not fabricated into the selection");
        AssertEquals(complete.Count, absent.Counters.ComparisonUniversePaths,
            "a selected path absent from the universe never extends the universe");
        AssertEquals(decisionsBefore, await CountDecisionsAsync(fixture.Db.DatabasePath),
            "a selected path absent from the universe writes no durable decision");

        var oneSided = await fixture.PlanAsync(Select(oneSidedPath));
        AssertEquals(1, oneSided.Outcomes.Count, "the one-endpoint saved baseline produces one decision");
        AssertEquals(SyncAuthorityPlannerOutcomeKind.Tombstone, oneSided.Outcomes[0].Kind,
            "the one-endpoint saved baseline records the absence instead of fabricating a create or move");
        AssertEquals(0, await fixture.RunnableOperationsAsync(),
            "the one-endpoint saved baseline plans no runnable operation");

        // Both reviewed sides: a local-only and a remote-only arrival keep their own direction.
        var twoSided = await Fixture.CreateAsync(_root, "c-two-sided", options);
        await twoSided.WriteLocalAsync("local-only-arrival.bin", "local only payload");
        await twoSided.WriteLocalAsync("both-sides.bin", "both sides payload");
        twoSided.ScriptFile("both-sides.bin", "both sides payload");
        twoSided.ScriptFile("remote-only-arrival.bin", "remote only payload");
        await twoSided.ObserveAsync();
        await twoSided.AdoptAsync();
        var both = await twoSided.PlanAsync(
            Select("local-only-arrival.bin", "remote-only-arrival.bin", "both-sides.bin"));
        var localOnly = both.Outcomes.Single(item => item.RelativePath == "local-only-arrival.bin");
        var remoteOnly = both.Outcomes.Single(item => item.RelativePath == "remote-only-arrival.bin");
        AssertEquals(SyncAuthorityPlannerOutcomeKind.Operation, localOnly.Kind,
            "the selected local-only arrival is planned");
        AssertEquals(SyncAuthorityPlannerOutcomeKind.Operation, remoteOnly.Kind,
            "the selected remote-only arrival is planned");
        AssertEquals(SyncAuthorityOperationKind.CreateRemote,
            (await twoSided.Db.LoadOperationAsync(localOnly.OperationId!)).Kind,
            "a local-only arrival keeps its remote-create direction");
        AssertEquals(SyncAuthorityOperationKind.CreateLocal,
            (await twoSided.Db.LoadOperationAsync(remoteOnly.OperationId!)).Kind,
            "a remote-only arrival keeps its local-create direction");
        AssertEquals(1, both.Outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged),
            "the selected two-sided baseline keeps its no-effect outcome");
        Assert(Describe(both.Outcomes).Distinct(StringComparer.Ordinal).Count() == both.Outcomes.Count,
            "no selected path is decided twice in one sparse pass");
    }

    // ----- D: the reviewed concurrency path decides each selected file exactly once ------------------

    private static async Task ReviewedBatchBoundariesAsync()
    {
        var sequential = await Fixture.CreateAsync(_root, "d-reviewed-sequential");
        var batched = await Fixture.CreateAsync(_root, "d-reviewed-batch");
        var arrivalPaths = new List<string>();
        for (var index = 0; index < 12; index++)
        {
            var path = "reviewed-row-" + index.ToString("D2", CultureInfo.InvariantCulture) + ".bin";
            sequential.ScriptFile(path, "reviewed payload " + index.ToString(CultureInfo.InvariantCulture));
            batched.ScriptFile(path, "reviewed payload " + index.ToString(CultureInfo.InvariantCulture));
            arrivalPaths.Add(path);
        }
        foreach (var fixture in new[] { sequential, batched })
        {
            await fixture.WriteLocalAsync("held.bin", "held payload");
            fixture.ScriptFile("held.bin", "held payload");
            await fixture.ObserveAsync();
            await fixture.AdoptAsync();
            // Case B proves actual scanner refusal for hardlink aliases. This case supplies an explicit
            // adversarial planner input to prove the unselected boundary cannot enroll another effect.
        }
        static SyncAuthorityPairObservationSet WithUnselectedAlias(SyncAuthorityPairObservationSet input)
        {
            var held = input.Local.Single(item => item.RelativePath == "held.bin");
            return input with { Local = input.Local.Append(held with
            { RelativePath = "held-alias.bin", MembershipId = held.MembershipId + "-adversarial-alias" }).ToArray() };
        }
        var sequentialInput = WithUnselectedAlias(await sequential.LoadObservationSetAsync());
        var observationSet = WithUnselectedAlias(await batched.LoadObservationSetAsync());
        AssertEquals(1, observationSet.SynchronizationPoints.Count, "one saved baseline was adopted");
        var observedMemberships = observationSet.Local.Count(item =>
            item.ObjectId == observationSet.SynchronizationPoints[0].ObjectId);
        AssertEquals(2, observedMemberships, "the explicit adversarial planner input carries two local memberships");
        var selection = Select(arrivalPaths[0], arrivalPaths[1]);
        var reviewedKinds = new Dictionary<string, SyncAuthorityOperationKind>(StringComparer.OrdinalIgnoreCase)
        {
            [arrivalPaths[0]] = SyncAuthorityOperationKind.CreateLocal,
            [arrivalPaths[1]] = SyncAuthorityOperationKind.CreateLocal
        };
        var sequentialRun = await sequential.PlanAsync(selection, reviewedKinds, sequential.Admission.CommandRequestId, 1, suppliedInput: sequentialInput);
        var batchedRun = await batched.PlanAsync(selection, reviewedKinds, batched.Admission.CommandRequestId, 16, suppliedInput: observationSet);
        Assert(sequentialRun.Outcomes.Select(item => item.Kind + "|" + item.RelativePath)
                .SequenceEqual(batchedRun.Outcomes.Select(item => item.Kind + "|" + item.RelativePath)),
            "the reviewed concurrency path reproduces the sequential reviewed result exactly");
        AssertEquals(selection.Count, batchedRun.Counters.ComparisonPathsDecided,
            "each selected reviewed file is decided exactly once");
        AssertEquals(1, batchedRun.Counters.BatchDrains,
            "the selected reviewed files drain as one reviewed batch");
        AssertEquals(selection.Count, batchedRun.Counters.BatchedPaths,
            "the reviewed batch covers exactly the selected reviewed files");
        var planned = batchedRun.Outcomes.Where(item =>
            item.Kind == SyncAuthorityPlannerOutcomeKind.Operation).ToArray();
        AssertEquals(selection.Count, planned.Length, "each selected reviewed file plans exactly one operation");
        AssertEquals(selection.Count, planned.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count(),
            "the reviewed batch keeps one immutable operation identity per selected file");
        AssertEquals(observationSet.SynchronizationPoints.Count, batchedRun.Counters.RelocatedBoundaryReuses,
            "the batch dependency boundary reuses each classified descriptor once");
        Assert(batchedRun.Outcomes.All(item => item.RelativePath != "held-alias.bin") &&
               batchedRun.Outcomes.All(item => item.RelativePath != "held.bin"),
            "an unselected alias boundary and its baseline are never decided by a sparse reviewed batch");
        AssertEquals(selection.Count, batchedRun.Counters.SelectedPathsVisited,
            "each selected reviewed file is visited once in the reviewed batch");
        AssertEquals(0, batchedRun.Counters.SelectedPathsSkipped,
            "no selected reviewed file is skipped after entering the reviewed batch");
    }

    // ----- E: sparse scale over 100000 unrelated observed rows ---------------------------------------

    private static async Task SparseScaleAsync()
    {
        var fixture = await Fixture.CreateAsync(_root, "e-sparse-scale");
        const int saved = ScaleSelectedFirstRun;
        for (var index = 0; index < saved; index++)
        {
            var path = "scale-saved-" + index.ToString("D2", CultureInfo.InvariantCulture) + ".bin";
            var content = "scale saved payload " + index.ToString(CultureInfo.InvariantCulture);
            await fixture.WriteLocalAsync(path, content);
            fixture.ScriptFile(path, content);
        }
        var unrelated = new List<string>(ScaleUnrelatedRows);
        for (var index = 0; index < ScaleUnrelatedRows; index++)
        {
            var path = "unrelated/row-" + index.ToString("D6", CultureInfo.InvariantCulture) + ".bin";
            fixture.ScriptFile(path, "unrelated payload " + index.ToString(CultureInfo.InvariantCulture));
            unrelated.Add(path);
        }
        await fixture.ObserveAsync();
        await fixture.AdoptAsync();
        var observationSet = await fixture.LoadObservationSetAsync();
        AssertEquals(saved, observationSet.SynchronizationPoints.Count, "the saved baselines were adopted");
        AssertEquals(saved, observationSet.Local.Count, "the local inventory holds only the saved files");
        AssertEquals(ScaleUnrelatedRows + saved, observationSet.Dropbox.Count,
            "the remote inventory holds the unrelated rows and the saved files");
        var universe = ScaleUnrelatedRows + saved;

        var ten = await fixture.PlanAsync(Select(unrelated.Take(ScaleSelectedFirstRun).ToArray()));
        var hundred = await fixture.PlanAsync(Select(unrelated
            .Skip(ScaleSelectedFirstRun).Take(ScaleSelectedSecondRun - ScaleSelectedFirstRun).ToArray()));
        foreach (var (run, selected) in new[]
                 {
                     (ten, ScaleSelectedFirstRun),
                     (hundred, ScaleSelectedSecondRun - ScaleSelectedFirstRun)
                 })
        {
            AssertEquals(saved, run.Counters.BaselineClassifications,
                "the sparse pass classifies every saved baseline once, independent of the selection");
            AssertEquals(saved, run.Counters.LocalMembershipLookups,
                "the sparse pass resolves each saved baseline membership once");
            AssertEquals(saved, run.Counters.RelocatedBoundaryReuses,
                "the boundary pass reuses each classified descriptor once");
            AssertEquals(universe, run.Counters.ComparisonUniversePaths,
                "the complete comparison universe is the whole retained cut");
            AssertEquals(universe, run.Counters.FilterMembershipTests,
                "the reviewed filter is tested once per complete universe path");
            AssertEquals(selected, run.Counters.SelectedComparisonPaths,
                "only the selected paths enter the selection");
            AssertEquals(selected, run.Counters.LexicalSortEntries,
                "the lexical sort covers only the selected paths, never the whole universe");
            AssertEquals(selected, run.Counters.ComparisonPathsDecided,
                "each selected path is decided exactly once");
            AssertEquals(0, run.Counters.EligibleDecisionEntries,
                "no unselected saved baseline enters the expensive decision loop");
            AssertEquals(saved, run.Counters.EligibilitySkips,
                "every unselected saved baseline is an eligibility-only skip");
            AssertEquals(universe - selected, run.Counters.UnselectedProgressAdvances,
                "the unselected universe remainder is accounted once");
            Assert(run.Counters.LexicalSortEntries * 1000 < run.Counters.ComparisonUniversePaths,
                "the lexical sort covers under a thousandth of the complete universe");
            var decisions = run.Progress.Where(item => item.Phase != "Preparing Comparison").ToArray();
            AssertEquals(selected + 1, decisions.Length,
                "only selected paths and the one final aggregate report emit decision callbacks");
            Assert(decisions.Where(item => item.Phase == "Comparing Files and Folders")
                    .All(item => run.Outcomes.Any(outcome => outcome.RelativePath == item.RelativePath)),
                "every per-path comparison callback names an actually selected outcome");
        }
        AssertEquals(ScaleSelectedFirstRun, ten.Counters.ComparisonPathsDecided,
            "the ten-path selection decides ten paths");
        AssertEquals(ScaleSelectedSecondRun - ScaleSelectedFirstRun, hundred.Counters.ComparisonPathsDecided,
            "the hundred-path selection decides one hundred paths");
        AssertEquals(ten.Counters.ComparisonUniversePaths, hundred.Counters.ComparisonUniversePaths,
            "the complete-context cost does not depend on the selection");
        AssertEquals(ten.Counters.FilterMembershipTests, hundred.Counters.FilterMembershipTests,
            "the reviewed filter is tested over the same complete universe in both selections");
        Assert(ten.Counters.LexicalSortEntries < hundred.Counters.LexicalSortEntries,
            "lexical work follows the selection, not the universe");
        Measurements.Add(new Dictionary<string, object?>
        {
            ["case"] = "E-measured",
            ["unrelated_rows"] = ScaleUnrelatedRows,
            ["saved_baselines"] = saved,
            ["universe_paths"] = universe,
            ["selected_ten"] = ten.Counters.SelectedComparisonPaths,
            ["elapsed_ms_ten"] = ten.ElapsedMilliseconds,
            ["allocated_bytes_ten"] = ten.AllocatedBytes,
            ["selected_hundred"] = hundred.Counters.SelectedComparisonPaths,
            ["elapsed_ms_hundred"] = hundred.ElapsedMilliseconds,
            ["allocated_bytes_hundred"] = hundred.AllocatedBytes,
            ["note"] = "Decision, sorting and eligibility work follow the reviewed selection. Loading the" +
                " complete observation, its identity dictionaries, the type-conflict and uncertainty indexes" +
                " and the comparison universe remains proportional to the total observed rows; no whole-engine" +
                " deadline or constant-time context claim is made here."
        });
    }

    // ----- F: cancellation, durable safety and truthful progress -------------------------------------

    private static async Task CancellationAndDurableSafetyAsync()
    {
        var fixture = await Fixture.CreateAsync(_root, "f-durable-safety");
        await fixture.WriteLocalAsync("stable-a.bin", "stable a");
        fixture.ScriptFile("stable-a.bin", "stable a");
        await fixture.WriteLocalAsync("stable-b.bin", "stable b");
        fixture.ScriptFile("stable-b.bin", "stable b");
        fixture.ScriptFile("arrival-a.bin", "arrival a");
        fixture.ScriptFile("arrival-b.bin", "arrival b");
        await fixture.ObserveAsync();
        await fixture.AdoptAsync();
        var observationSet = await fixture.LoadObservationSetAsync();
        var complete = CompleteSelection(observationSet);
        AssertEquals(2, observationSet.SynchronizationPoints.Count, "two saved baselines were adopted");

        var completions = 0;
        var cancelled = false;
        var decisionsBefore = await CountDecisionsAsync(fixture.Db.DatabasePath);
        SyncAuthorityPlanningContextObservation.CompletedForTest = _ => completions++;
        try
        {
            using var token = new CancellationTokenSource();
            var planner = new SyncAuthorityReconciliationPlanner(fixture.Db, fixture.Options, fixture.Admission, complete, null,
                null, null, null, step =>
                {
                    if (step.Phase == "Preparing Comparison" && step.CompletedChecks > 0) token.Cancel();
                    return Task.CompletedTask;
                });
            await planner.PlanAsync(observationSet,
                SyncAuthorityReconciliationPlanner.ObservationSetIdentity(observationSet), null, null,
                token.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            SyncAuthorityPlanningContextObservation.CompletedForTest = null;
        }
        Assert(cancelled, "cancellation during the preparation pass is observed");
        AssertEquals(0, completions, "a cancelled planning invocation never publishes a completed context");
        AssertEquals(decisionsBefore, await CountDecisionsAsync(fixture.Db.DatabasePath),
            "cancellation during preparation writes no durable decision");
        AssertEquals(0, await fixture.RunnableOperationsAsync(),
            "cancellation during preparation plans no operation");

        var mismatchKinds = new Dictionary<string, SyncAuthorityOperationKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["arrival-a.bin"] = SyncAuthorityOperationKind.CreateRemote
        };
        var mismatch = await fixture.PlanAsync(Select("arrival-a.bin"), mismatchKinds);
        AssertEquals(1, mismatch.Outcomes.Count, "the mismatched reviewed path produces one decision");
        AssertEquals(SyncAuthorityPlannerOutcomeKind.Blocked, mismatch.Outcomes[0].Kind,
            "a reviewed kind that no longer matches the observed change is refused");
        Assert(mismatch.Outcomes[0].Reason.Contains("reviewed", StringComparison.Ordinal),
            "the refusal keeps its retained reviewed-action reason");
        AssertEquals(0, await fixture.RunnableOperationsAsync(),
            "a refused reviewed kind plans no effect");

        var unchanged = await fixture.PlanAsync(complete);
        AssertEquals(0, await CountRoutineDecisionsAsync(fixture.Db.DatabasePath),
            "unchanged and skipped outcomes write no durable decision row");
        AssertEquals(2, unchanged.Outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged),
            "both matching saved baselines keep their unchanged outcome");
        AssertEquals(2, unchanged.Progress.Count(item => item.Phase == "Checking Saved Baselines"),
            "each eligible saved baseline reports exactly one comparison callback");
        var comparisons = unchanged.Progress.Where(item => item.Phase != "Preparing Comparison").ToArray();
        var preparation = unchanged.Progress.Where(item => item.Phase == "Preparing Comparison").ToArray();
        foreach (var phase in new[] { preparation, comparisons })
        {
            var reported = phase.Select(item => item.CompletedChecks).ToArray();
            Assert(reported.SequenceEqual(reported.OrderBy(value => value)),
                "progress never goes backwards within one measured unit and phase");
        }
        var expectedTotal = observationSet.SynchronizationPoints.Count +
            unchanged.Counters.ComparisonUniversePaths;
        Assert(comparisons.All(item => item.TotalChecks == expectedTotal),
            "comparison reports retain the complete baseline plus universe total");
        Assert(preparation.All(item => item.TotalChecks == observationSet.SynchronizationPoints.Count),
            "preparation truthfully reports its distinct saved-baseline unit");
        AssertEquals(expectedTotal, comparisons[^1].CompletedChecks,
            "the terminal report accounts for the complete comparison context");
        AssertEquals(unchanged.Counters.BaselineClassifications,
            unchanged.Counters.EligibilitySkips + unchanged.Counters.EligibleDecisionEntries,
            "the classifies, skips and decisions of one invocation reconcile exactly");

        var empty = await fixture.PlanAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        AssertEquals(0, empty.Outcomes.Count, "an empty reviewed selection decides nothing");
        AssertEquals(0, empty.Progress.Count(item => item.Phase == "Checking Saved Baselines"),
            "an eligibility-only skipped baseline emits no per-skip comparison callback");
        AssertEquals(expectedTotal, empty.Progress[^1].CompletedChecks,
            "an all-skipped pass still accounts for the complete context before its terminal report");
    }
}
