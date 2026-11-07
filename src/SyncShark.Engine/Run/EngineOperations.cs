using System.Globalization;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Translates commands into the host's shared lifetime and atomic persistence owners.</summary>
public sealed class EngineOperations(string productRoot, Action<string> progress,
    int renderWidth = RunLog.HumanWidth)
{
    private readonly EngineHost? _host;
    private EngineSettings? _loadedSettings;

    internal EngineOperations(string productRoot, Action<string> progress, EngineHost host)
        : this(productRoot, progress) => _host = host ?? throw new ArgumentNullException(nameof(host));

    public IReadOnlyList<PairConfig> ListPairs() => Read(store => store.Pairs(), Array.Empty<PairConfig>());
    public PairConfig Pair(string pairId) => Read(store => RequirePair(store, pairId), null as PairConfig) ?? throw Unknown(pairId);
    public IReadOnlyList<StatusInfo> Status(string? pairId) => Read<IReadOnlyList<StatusInfo>>(store =>
        pairId is null ? store.Pairs().Select(pair => Describe(store, pair)).ToArray() :
        [Describe(store, RequirePair(store, pairId))], []);
    public IReadOnlyList<Issue> Issues(string pairId, IssueKind? kind, bool ignored = false) => Read<IReadOnlyList<Issue>>(store =>
    {
        RequirePair(store, pairId);
        return store.OpenIssuePage(pairId, null, null, kind is null ? null : [kind.Value], int.MaxValue, 0, ignored).Page.Select(row => PairSafety.ForReview(row.Issue)).ToArray();
    }, []);

    /// <summary>One bounded page of open issues and its total, read through the storage owner.</summary>
    public (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenIssuePage(string? pairId, string? account,
        string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset, bool ignored = false) =>
        Read<(IReadOnlyList<(string PairId, Issue Issue)> Page, int Total)>(
            store => ReviewedPage(store.OpenIssuePage(pairId, account, search, kinds, limit, offset, ignored)), ([], 0));

    /// <summary>Every open issue of the requested kinds and its total from one read-only snapshot.</summary>
    public (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) OpenIssueSnapshot(
        IReadOnlyList<IssueKind>? kinds, int pageSize) =>
        Read<(IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total)>(
            store => ReviewedPage(store.OpenIssueSnapshot(kinds, pageSize)), ([], 0));

    private static (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) ReviewedPage(
        (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) page) =>
        (page.Page.Select(row => (row.PairId, PairSafety.ForReview(row.Issue))).ToArray(), page.Total);

    /// <summary>The pair's retained Repair summary, or null; one pair check and one narrow read (2 statements).</summary>
    public RepairSummary? RetainedRepair(string pairId) => Read(store =>
    {
        _ = RequirePair(store, pairId);
        return store.LastRepair(pairId);
    }, null);

    public int PageTake => (_host?.Settings ?? (_loadedSettings ??= EngineHost.LoadSettings(productRoot)))
        .StoragePageSize;
    public static IReadOnlyList<string> IgnoreRules(PairConfig pair, bool folderKeep) =>
        folderKeep ? pair.IgnoredFolderKeepRules ?? [] : pair.IgnoreRules;

    public Task<UiPreferences> ReadUiPreferencesAsync(CancellationToken ct) =>
        (_host ?? throw new InvalidOperationException()).ReadPreferencesAsync(ct);
    public Task<UiPreferences> UpdateUiPreferencesAsync(UiPreferencesUpdate update, CancellationToken ct) =>
        (_host ?? throw new InvalidOperationException()).UpdatePreferencesAsync(update, ct);

    public Task<PairConfig> AddPairAsync(string name, string localRoot, string dropboxRoot, string connectionId,
        FirstRunPolicy? firstRun, CancellationToken ct)
    {
        if (!PathNames.IsDropboxRoot(dropboxRoot))
            throw new EngineRequestException(TextKey.DropboxPathInvalid, (TextArg.Path, dropboxRoot));
        if (!LocalPaths.RootExists(localRoot))
            throw new EngineRequestException(TextKey.LocalRootMissing, (TextArg.Path, localRoot));
        return WithHostAsync(async host =>
        {
            if (new DropboxConnectionStore(host.Authority).Load(connectionId) is null)
                throw new EngineRequestException(TextKey.AccountMissing, (TextArg.Account, connectionId));
            var pair = new PairConfig(ResourceNames.NewId(), name, localRoot, dropboxRoot, connectionId,
                [], [], firstRun ?? host.Settings.DefaultFirstRunPolicy, true, null);
            return await host.ChangeGenerationAsync(pair, (transition, store, current, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (current.ContainsKey(pair.PairId)) throw new EngineRequestException(TextKey.ImportExists);
                store.SavePair(pair);
                return Task.FromResult(pair);
            }, ct).ConfigureAwait(false);
        }, false, ct);
    }

    public Task<(PairConfig Pair, BaselineImportCounts Counts)> ImportPairAsync(string v2PairId, CancellationToken ct) => WithHostAsync(async host =>
    {
        using var imported = PairImport.Open(productRoot, v2PairId, host.Settings, host.Store);
        return await host.ChangeGenerationAsync(imported.Pair, async (transition, store, current, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (current.ContainsKey(imported.Pair.PairId)) throw new EngineRequestException(TextKey.ImportExists);
            await imported.ImportAsync(store, ct => host.ReadImportRootsAsync(imported.Pair, ct), token).ConfigureAwait(false);
            return (imported.Pair, imported.Count);
        }, ct).ConfigureAwait(false);
    }, false, ct);

    public async Task RemovePairAsync(string pairId, CancellationToken ct) =>
        await WithPairHostAsync(pairId, (host, pair) => host.ChangeGenerationAsync(pair,
            (transition, store, current, token) =>
            {
                token.ThrowIfCancellationRequested();
                store.RemovePair(pairId);
                return Task.FromResult(pair);
            }, ct), false, ct).ConfigureAwait(false);

    public Task<PairConfig> SetEnabledAsync(string pairId, bool enabled, CancellationToken ct) =>
        UpdatePairAsync(pairId, pair => pair with { Enabled = enabled }, null, ct);
    public Task<PairConfig> ExcludeAsync(string pairId, Exclusion exclusion, CancellationToken ct) =>
        UpdatePairAsync(pairId, pair =>
        {
            RequireExclusionPath(pair, exclusion);
            return pair with { Exclusions = [.. pair.Exclusions, exclusion] };
        }, null, ct);
    public Task<PairConfig> IncludeAsync(string pairId, Side side, string path, CancellationToken ct)
    {
        var key = PathNames.KeyFor(side, path);
        return UpdatePairAsync(pairId, pair => pair with
        {
            Exclusions = pair.Exclusions.Where(existing => existing.Side != side ||
                PathNames.KeyFor(existing.Side, existing.Path) != key).ToArray()
        }, key, ct);
    }

    public Task<PairConfig> SetIgnoreRulesAsync(string pairId, string rules, bool folderKeep, CancellationToken ct)
    {
        var normalized = NormalizeRules(rules).Select(SyncIgnorePolicy.Format).ToArray();
        return UpdatePairAsync(pairId, pair => IgnoreRules(pair, folderKeep).SequenceEqual(normalized, StringComparer.Ordinal)
            ? pair : folderKeep ? pair with { IgnoredFolderKeepRules = normalized.Length == 0 ? null : normalized }
                : pair with { IgnoreRules = normalized }, null, ct);
    }

    private Task<PairConfig> UpdatePairAsync(string pairId, Func<PairConfig, PairConfig> update, string? reincludedKey,
        CancellationToken ct) =>
        WithPairHostAsync(pairId, (host, pair) => host.ChangeGenerationAsync(pair,
            (transition, store, current, token) =>
            {
                var expected = Required(current, pairId);
                var next = update(expected.Pair);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(next, expected.Pair) || reincludedKey is not null)
                    store.SavePairDecision(expected.Pair, expected.Generation, next, null, false, reincludedKey);
                return Task.FromResult(next);
            }, ct), false, ct);

    public Task<PairConfig> BindRootsAsync(string pairId, CancellationToken ct) =>
        WithPairHostAsync(pairId, (host, pair) => BindRootsAsync(host, pair,
            host.Store.Issues(pairId, [IssueKind.RootIdentity]).SingleOrDefault(), ct), false, ct);

    private static Task<PairConfig> BindRootsAsync(EngineHost host, PairConfig pair, Issue? issue,
        CancellationToken ct) => host.ChangeGenerationAsync(pair,
            async (transition, store, current, token) =>
            {
                var expected = Required(current, pair.PairId);
                var observed = await PairSession.ReadConfigurationRootsAsync(store, expected.Pair, host.Settings,
                    host.Http, host.Authority, host, transition, expected.Generation, token).ConfigureAwait(false);
                PairSafety.ValidateConfigurationRoots(store, expected.Pair, expected.Generation, observed, true);
                token.ThrowIfCancellationRequested();
                if (expected.Pair.Roots == observed)
                {
                    store.ReconcileRootIdentity(pair.PairId, expected.Generation, expected.Pair.Roots, issue,
                        StateChange.Empty with { Close = [(string.Empty, new[] { IssueKind.RootIdentity })] });
                    return expected.Pair;
                }
                var next = expected.Pair with { Roots = observed };
                store.SavePairDecision(expected.Pair, expected.Generation, next, issue, false, null);
                return next;
            }, ct);

    public async Task<RepairSummary> RepairAsync(string pairId, CancellationToken ct, bool perFileDetail = false)
    {
        var lookupStarted = TimeProvider.System.GetTimestamp();
        progress(RunLog.StageDetail(TextKey.StagePairLookup));
        var pair = ExistingPair(pairId);
        using var log = OpenLog(pair.PairId,
            (_host?.Settings ?? EngineHost.LoadSettings(productRoot)).LogBufferCharacters, perFileDetail);
        log.Event(new RunEvent(RunEventKind.Stage, nameof(TextKey.StagePairLookup), string.Empty,
            RunLog.StageDetail(TextKey.StagePairLookup, ended: true, count: RunLog.CompletedOperation,
                elapsed: TimeProvider.System.GetElapsedTime(lookupStarted)), nameof(RunEvent.Ended)));
        try
        {
            return await WithHostAsync(host => RepairCoreAsync(host, pairId, null, ct, log, perFileDetail), false, ct, log)
                .ConfigureAwait(false);
        }
        catch (Exception error) { log.Failed(error); throw; }
    }

    private async Task<RepairSummary> RepairCoreAsync(EngineHost host, string pairId, IReadOnlySet<long>? retryIssues,
        CancellationToken ct, RunLog? startupLog = null, bool perFileDetail = false)
    {
        using var ownedLog = startupLog is null
            ? OpenLog(pairId, host.Settings.LogBufferCharacters, perFileDetail) : null;
        var log = startupLog ?? ownedLog!;
        try
        {
            await host.RecomposeCurrentAsync(pairId, ct, log).ConfigureAwait(false);
            if (host.Store.Obligated(pairId))
            {
                // An explicit Repair keeps its drive-decision refusal; the scheduler callers simply skip the pair.
                if (host.Store.ReadPairExecution(pairId) is { } current &&
                    host.Admission(pairId, current.Pair) == EngineHost.PairAdmission.AwaitingDecision)
                    throw new EngineRequestException(TextKey.PairAwaitingDecision, (TextArg.Pair, pairId));
                return await host.RunPrerequisiteAsync(pairId, ct, log).ConfigureAwait(false) ??
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            }
            await host.EnsureSourceBasesAsync(pairId, ct, log).ConfigureAwait(false);
            try
            {
                return await host.DispatchSessionAsync(pairId, () => log,
                    (session, token) => Repair.RunAsync(session, token, retryIssues), ct, log).ConfigureAwait(false);
            }
            finally { host.WakeAfterOperation(pairId); }
        }
        catch (Exception error) { log.Failed(error); throw; }
    }

    public Task<ExcludePreview> ExcludePreviewAsync(string pairId, Exclusion exclusion, CancellationToken ct)
    {
        return WithPairHostAsync(pairId, async (host, pair) =>
        {
            RequireExclusionPath(pair, exclusion);
            await host.RecomposeCurrentAsync(pairId, ct).ConfigureAwait(false);
            return await host.ObserveAsync(pairId,
                async (registration, pair, generation, token) =>
                {
                    if (!host.TryHoldGeneration(registration)) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
                    var session = host.OpenForRegistration(registration, pair, generation, null);
                    return await session.Syncer.PreviewExclusionAsync(exclusion, token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
        }, true, ct);
    }

    public Task<ResolveSummary> ResolveAsync(string pairId, long? issueId,
        ResolutionChoice choice, string? newName, FirstRunPolicy? firstRun, bool all, string? rules, CancellationToken ct,
        string? reviewed = null)
    {
        if (!Resolutions.RequestValid(issueId is not null, all, choice, newName is not null, firstRun is not null, rules))
            throw BadChoice(issueId, choice);
        IReadOnlyList<SyncIgnoreRule>? keepRules = rules is null ? null : NormalizeRules(rules);
        return WithPairHostAsync<ResolveSummary>(pairId,
            async (host, pair) =>
        {
            var issue = all ? null : host.Store.IssueById(pairId, issueId!.Value) ?? throw StaleIssue(issueId!.Value);
            if (issue is not null && reviewed is not null) PairSafety.RequireIssue(issue, reviewed);
            if (issue is not null && !Resolutions.Allows(issue.Kind, issue.Cause, choice)) throw BadChoice(issueId, choice);
            if (choice is ResolutionChoice.Ignore or ResolutionChoice.Unignore)
            {
                if (!host.Store.SetIssueIgnored(pairId, issue!, choice == ResolutionChoice.Ignore))
                    throw StaleIssue(issue.Id);
                return new ResolveSummary(SyncOutcome.InSync, false, null);
            }
            if (choice == ResolutionChoice.KeepFolderAlways && keepRules!.Count == 0) throw BadChoice(issueId, choice);
            if (choice == ResolutionChoice.BindRoots)
            {
                await BindRootsAsync(host, pair, issue, ct).ConfigureAwait(false);
                return new ResolveSummary(SyncOutcome.InSync, host.Store.IssueById(pairId, issue!.Id) is null, null);
            }
            if (choice is ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally)
            {
                await host.ChangeGenerationAsync(pair,
                    async (transition, store, current, token) =>
                    {
                        var expected = Required(current, pairId);
                        if (choice == ResolutionChoice.FirstSync)
                        {
                            var roots = await PairSession.ReadConfigurationRootsAsync(store, expected.Pair, host.Settings,
                                host.Http, host.Authority, host, transition, expected.Generation, token).ConfigureAwait(false);
                            PairSafety.ValidateConfigurationRoots(store, expected.Pair, expected.Generation, roots, false);
                        }
                        token.ThrowIfCancellationRequested();
                        var next = expected.Pair with { Drive = null, FirstRun = firstRun ?? expected.Pair.FirstRun };
                        store.SavePairDecision(expected.Pair, expected.Generation, next, issue,
                            choice == ResolutionChoice.FirstSync, null);
                        return next;
                    }, ct).ConfigureAwait(false);
                return new ResolveSummary(SyncOutcome.InSync, host.Store.IssueById(pairId, issue!.Id) is null, null);
            }
            if (all)
            {
                var failures = host.Store.Issues(pairId, [IssueKind.Failure]).ToArray();
                var outcome = SyncOutcome.InSync;
                var succeeded = 0;
                RepairSummary? repair = null;
                var operations = new EngineOperations(productRoot, progress, host);
                foreach (var failure in failures)
                {
                    if (host.Store.IssueById(pairId, failure.Id) is null)
                    {
                        succeeded++;
                        continue;
                    }
                    try
                    {
                        var result = await operations.ResolveAsync(pairId, failure.Id, choice, newName,
                            firstRun, false, rules, ct).ConfigureAwait(false);
                        if (!result.Outcome.Succeeded()) outcome = result.Outcome;
                        if (result.Resolved) succeeded++;
                        repair = result.Repair ?? repair;
                    }
                    catch (Exception error) when (error is EngineRequestException or LiveAdmissionStaleException)
                    {
                        if (host.Store.IssueById(pairId, failure.Id) is null) succeeded++;
                        else outcome = new SyncOutcome(ActionKind.None, NoneReason.Held, null, IssueKind.Failure, false);
                    }
                }
                return new ResolveSummary(outcome, succeeded == failures.Length, repair)
                { Retried = failures.Length, Succeeded = succeeded };
            }
            if (choice == ResolutionChoice.Retry && issue is { Cause: TextKey.RequiredRepairRetry } &&
                issue.Subject.Items.Count == 0)
            {
                var repair = await host.RunPrerequisiteAsync(pairId, ct, expected: issue).ConfigureAwait(false);
                return new ResolveSummary(SyncOutcome.InSync, host.Store.IssueById(pairId, issue.Id) is null, repair);
            }
            await host.RecomposeCurrentAsync(pairId, ct).ConfigureAwait(false);
            try
            {
                var result = await host.DispatchAsync(pairId,
                    async (registration, admitted, generation, token) =>
                    {
                        // Ownership and kind validation precede opening the run log (CV/resolve contract).
                        PairSafety.RequireRunnable(host, registration, admitted, generation);
                        using var log = OpenLog(pairId, host.Settings.LogBufferCharacters);
                        var session = host.OpenForRegistration(registration, admitted, generation, log);
                        if (choice == ResolutionChoice.Retry)
                        {
                            var retry = host.Store.ReadItemRetryState(pairId, [issue!.Id], issue).GetValueOrDefault(issue.Id);
                            if (retry is { Dispatch: ItemRetryDispatch.LocalRecovery,
                                PendingOperationId: { } operation, PendingGeneration: { } pendingGeneration })
                            {
                                await session.Syncer.RecoverAsync(new RecoveryTarget(operation, pendingGeneration, null, null),
                                    RecoveryChoice.Check, token).ConfigureAwait(false);
                                return new ResolutionOutcome(SyncOutcome.InSync, host.Store.IssueById(pairId, issue.Id) is null);
                            }
                        }
                        return await PairSafety.ResolveSelectedAsync(session, issue!, choice, newName, keepRules, token)
                            .ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
                return new ResolveSummary(result.Outcome, result.Resolved, null);
            }
            finally { host.WakeAfterOperation(pairId); }
        }, false, ct);
    }

    public IReadOnlyDictionary<long, IssueRetryView> ReadIssueRetryViews(string pairId, IReadOnlyList<long> issueIds) =>
        Read<IReadOnlyDictionary<long, IssueRetryView>>(store =>
        {
            var pair = store.ReadPairExecution(pairId) ?? throw Unknown(pairId);
            var states = store.ReadItemRetryState(pairId, issueIds);
            // Every requested issue keeps its entry: a missing candidate is the batch's Unavailable, never the
            // enum default. Recovery is stated before any host state, as the single lookup did.
            var hostStates = _host?.RetryStates(pair.Pair, pair.Generation, states);
            return states.ToDictionary(value => value.Key, value => new IssueRetryView(
                value.Value?.Dispatch == ItemRetryDispatch.LocalRecovery ? RetryPresentationState.RecoveryRequired :
                hostStates?.GetValueOrDefault(value.Key) ?? RetryPresentationState.Unavailable));
        }, new Dictionary<long, IssueRetryView>());

    public RecoveryView Recovery(string pairId, string operationId) => Read(store =>
    {
        RequirePair(store, pairId);
        return store.Pending(pairId) switch
        {
            CurrentPending current when current.Value.OperationId == operationId => PendingView(current),
            LegacyPending legacy when legacy.RawToken == operationId => PendingView(legacy),
            _ => FolderView(store, pairId, store.FolderRemoval(pairId, operationId))
        };
    }, null as RecoveryView) ?? throw Unknown(pairId);

    public IReadOnlyList<RecoveryView> RecoveryList(string pairId, string? afterOperationId) =>
        Read<IReadOnlyList<RecoveryView>>(store =>
        {
            RequirePair(store, pairId);
            IEnumerable<RecoveryView> Pending()
            {
                switch (store.Pending(pairId))
                {
                    case CurrentPending current:
                        yield return PendingView(current);
                        break;
                    case LegacyPending legacy:
                        yield return PendingView(legacy);
                        break;
                }
            }
            var pending = Pending().ToDictionary(value => value.OperationId, StringComparer.Ordinal);
            var folders = store.ActiveFolderRemovals(pairId, CancellationToken.None)
                .Where(value => afterOperationId is null || string.CompareOrdinal(value.OperationId, afterOperationId) > 0)
                .OrderBy(value => value.OperationId, StringComparer.Ordinal).Take(PageTake).ToArray();
            return pending.Keys.Concat(folders.Select(value => value.OperationId))
                .Where(id => afterOperationId is null || string.CompareOrdinal(id, afterOperationId) > 0)
                .OrderBy(id => id, StringComparer.Ordinal).Take(PageTake)
                .Select(id => pending.TryGetValue(id, out var value) ? value :
                    FolderView(store, pairId, folders.First(operation => operation.OperationId == id))).ToArray();
        }, []);

    private static readonly RecoveryChoice[] PendingChoices =
        [RecoveryChoice.Check, RecoveryChoice.AcceptCurrent, RecoveryChoice.AbandonIncoming];

    // One projection for a recorded pending operation, shared by the single-operation and list reads.
    private static RecoveryView PendingView(PendingRead pending) => pending switch
    {
        CurrentPending current => new RecoveryView(current.Value, PendingChoices),
        LegacyPending legacy => new RecoveryView(legacy, PendingChoices),
        _ => throw new InvalidDataException()
    };

    private RecoveryView FolderView(IStateStore store, string pairId, FolderRemovalState operation) => new(operation.OperationId,
        operation.Generation, operation.RootEntry.Path, operation.Phase,
        Executor.FolderDeleteRunner.Status(operation.Phase),
        operation.Cause, operation.RetirementDisposition,
        operation.Active && FolderRetirement.CanAcknowledge(Executor.Recovery.Members(store, pairId, operation.OperationId, PageTake))
            ? [RecoveryChoice.Check, RecoveryChoice.AcceptCurrent] : [RecoveryChoice.Check]);

    public RecoveryItemsView RecoveryItems(string pairId, string operationId, long afterOrdinal, int pageSize) =>
        Read(store =>
        {
            RequirePair(store, pairId);
            store.FolderRemoval(pairId, operationId);
            var items = store.FolderRemovalItems(pairId, operationId, afterOrdinal, pageSize).ToArray();
            var last = items.Length == 0 ? afterOrdinal : items[^1].Ordinal;
            var more = items.Length == pageSize && store.FolderRemovalItems(pairId, operationId, last, pageSize).Any();
            return new RecoveryItemsView(items, last, more);
        }, null as RecoveryItemsView) ?? throw Unknown(pairId);

    public Task<RemovalResult> ResolveRecoveryAsync(string pairId, string operationId, long generation,
        RecoveryChoice choice, bool confirmed, long? item, long? itemGeneration, CancellationToken ct)
    {
        if (!confirmed || !Enum.IsDefined(choice) || item.HasValue != itemGeneration.HasValue)
            throw new ArgumentException(null, nameof(choice));
        return WithPairHostAsync(pairId, async (host, _) =>
        {
            try
            {
                return await host.DispatchSessionAsync(pairId, () => OpenLog(pairId, host.Settings.LogBufferCharacters),
                    (session, token) => session.Syncer.RecoverAsync(
                        new RecoveryTarget(operationId, generation, item, itemGeneration), choice, token),
                    ct).ConfigureAwait(false);
            }
            finally { host.WakeAfterOperation(pairId); }
        }, false, ct);
    }

    /// <summary>The Edge's way to the one resolve-request shape rule; Edge does not reach the Planner.</summary>
    public static bool RequestValid(bool hasIssue, bool all, ResolutionChoice choice, bool hasName, bool hasFirstRun,
        string? rules) => Resolutions.RequestValid(hasIssue, all, choice, hasName, hasFirstRun, rules);

    public static IReadOnlyList<ResolutionChoice> ResolutionChoices(Issue issue) => Enum.GetValues<ResolutionChoice>()
        .Where(choice => Resolutions.Allows(issue.Kind, issue.Cause, choice) &&
            (choice != ResolutionChoice.Ignore || !issue.Ignored) &&
            (choice != ResolutionChoice.Unignore || issue.Ignored)).ToArray();

    public Task<(AutoStatus? Status, string? Detail)> HostStatusAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(_host?.Status() ?? (null, null)); }
    public async Task<(AutoStatus? Status, string? Detail)> HostStartAsync(CancellationToken ct)
    { var host = _host ?? throw new InvalidOperationException(); await host.StartAsync(ct); return host.Status(); }
    public async Task<(AutoStatus? Status, string? Detail)> HostPauseAsync(string? pairId, CancellationToken ct)
    { var host = _host ?? throw new InvalidOperationException(); await host.PauseAsync(pairId, ct); return host.Status(); }
    public async Task<(AutoStatus? Status, string? Detail)> HostResumeAsync(string? pairId, CancellationToken ct)
    { var host = _host ?? throw new InvalidOperationException(); await host.ResumeAsync(pairId, ct); return host.Status(); }
    public async Task<(AutoStatus? Status, string? Detail)> HostStopAsync(CancellationToken ct)
    { var host = _host ?? throw new InvalidOperationException(); await host.StopAsync(ct); return host.Status(); }

    public IReadOnlyList<(string Name, string Value, bool Overridden)> Settings() =>
        SettingsParser.List(EngineHost.DefaultsJson(), SettingsFile.Read(productRoot));
    public IReadOnlyList<(string Name, string Value, bool Overridden)> SetSetting(string name, string value)
    {
        var json = SettingsParser.WithOverride(SettingsFile.Read(productRoot), name, value);
        var settings = SettingsParser.List(EngineHost.DefaultsJson(), json);
        // The policy of the validated replacement document, never of the old one: an invalid saved value is
        // correctable (#135).
        var policy = SettingsParser.Parse(EngineHost.DefaultsJson(), json).AtomicReplace;
        if (_host is not null) SettingsFile.Write(productRoot, json, policy);
        else
        {
            using var instanceLock = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
            SettingsFile.Write(productRoot, json, policy);
        }
        return settings;
    }

    private T Read<T>(Func<IStateStore, T> read, T absent)
    {
        if (_host is not null) return read(_host.Store);
        using var store = StateStore.OpenReadOnly(DatabasePath);
        return store is null ? absent : read(store);
    }
    // The one pre-open existence answer for a pair command: an unknown pair is refused before a store is opened
    // for writing, and the same read supplies the definition the transition needs.
    private async Task<T> WithPairHostAsync<T>(string pairId, Func<EngineHost, PairConfig, Task<T>> operation,
        bool preview, CancellationToken ct)
    {
        var existing = ExistingPair(pairId);
        return await WithHostAsync(host => operation(host, existing), preview, ct).ConfigureAwait(false);
    }
    private PairConfig ExistingPair(string pairId)
    {
        if (_host is not null) return RequirePair(_host.Store, pairId);
        using var probe = StateStore.OpenReadOnly(DatabasePath) ?? throw Unknown(pairId);
        return probe.Pair(pairId) ?? throw Unknown(pairId);
    }
    private async Task<T> WithHostAsync<T>(Func<EngineHost, Task<T>> operation, bool preview, CancellationToken ct,
        RunLog? startupLog = null)
    {
        ct.ThrowIfCancellationRequested();
        if (_host is not null) return await operation(_host).ConfigureAwait(false);
        await using var monitor = preview ? null : new Monitor(TimeProvider.System);
        if (monitor is not null)
        {
            monitor.Observe(() => startupLog?.ReportProgress(
                Text.Get(TextKey.StatusStillQuiet)) == true);
            monitor.Start(quietOnly: true);
        }
        startupLog?.Event(RunEvent.Stage(TextKey.StageInstanceLock));
        using var instanceLock = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        startupLog?.Event(RunEvent.Stage(TextKey.StageInstanceLock, ended: true, count: RunLog.CompletedOperation));
        await using var host = EngineHost.Compose(productRoot, progress, instanceLock.StateAuthority,
            preview ? EngineHost.Mode.Preview : EngineHost.Mode.Direct, startupLog, monitor);
        return await operation(host).ConfigureAwait(false);
    }
    private static (PairConfig Pair, long Generation) Required(
        IReadOnlyDictionary<string, (PairConfig Pair, long Generation)> values, string pairId) =>
        values.TryGetValue(pairId, out var value) ? value : throw Unknown(pairId);
    private static PairConfig RequirePair(IStateStore store, string pairId) => store.Pair(pairId) ?? throw Unknown(pairId);
    private static EngineRequestException Unknown(string pairId) => new(TextKey.PairUnknown, (TextArg.Pair, pairId));
    private static EngineRequestException StaleIssue(long id) => new(TextKey.ResolveStale,
        (TextArg.Id, id.ToString(CultureInfo.InvariantCulture)));
    private static EngineRequestException BadChoice(long? id, ResolutionChoice choice) => new(TextKey.ResolveBadChoice,
        (TextArg.Id, id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty), (TextArg.Choice, choice.ToString()));
    // Preview and apply share the side's existing path owner; neither states the rule itself and neither performs
    // provider I/O to learn it.
    private static void RequireExclusionPath(PairConfig pair, Exclusion exclusion)
    {
        if (exclusion.Side != Side.Local)
        {
            DropboxPaths.ContainedRelative(exclusion.Path);
            return;
        }
        try { LocalPaths.Full(pair.LocalRoot, exclusion.Path); }
        catch (ItemFailedException error)
        {
            throw new EngineRequestException(error.Cause, (TextArg.Path, error.Path),
                (TextArg.Message, error.InnerException?.Message ?? string.Empty));
        }
    }

    private static IReadOnlyList<SyncIgnoreRule> NormalizeRules(string specification)
    {
        try { return SyncIgnorePolicy.Normalize(SyncIgnorePolicy.ParseForEdit(specification)); }
        catch (InvalidDataException error) { throw new EngineRequestException(TextKey.IgnoreRulesInvalid, (TextArg.Message, error.Message)); }
    }
    private static StatusInfo Describe(IStateStore store, PairConfig pair) => new(pair, store.LastRepair(pair.PairId),
        store.IssueCounts(pair.PairId, ignored: false), store.Pending(pair.PairId) is not NoPending, store.FirstRepairDone(pair.PairId));
    private string DatabasePath => StateStore.DatabasePath(productRoot);
    private RunLog OpenLog(string pairId, int bufferCharacters, bool perFileDetail = false) => RunLog.Open(
        Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder), pairId,
        ResourceNames.NewId(), progress, bufferCharacters, perFileDetail: perFileDetail,
        title: TextKey.HumanTitleRepair, width: renderWidth,
        mode: _host is null ? TextKey.HumanModeDirect : TextKey.HumanModeHosted);

    /// <summary>Read-only stored-log view through the shared human projection; never creates, prunes or locks.
    /// Human blocks are streamed one at a time through the caller's existing output transport, so memory stays
    /// bounded by one record; structured output returns the same canonical six-field records.</summary>
    public LogView ReadLogView(string pairId, string runId, bool perFileDetail, bool structured, CancellationToken ct)
    {
        var folder = Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder);
        var path = LogFiles.Resolve(folder, pairId, runId);
        if (path is null) return new LogView(LogViewStatus.Invalid);
        var human = new HumanLog(pairId, runId, path, TextKey.HumanTitleRun, perFileDetail, renderWidth);
        var records = structured ? new List<LogRecord>() : null;
        var malformed = 0;
        var lineNumber = 0;
        var status = LogFiles.Read(path, (line, wellFormed) =>
        {
            lineNumber++;
            if (records is not null)
            {
                if (wellFormed && RunLog.TryDecodeRecord(line, out var record)) records.Add(record);
                else malformed++;
                return;
            }
            var block = human.AcceptFileLine(lineNumber, line, wellFormed);
            if (block is not null) progress(block);
        }, ct, out var unfinished);
        if (status == LogFiles.ReadStatus.NotFound) return new LogView(LogViewStatus.NotFound);
        if (status == LogFiles.ReadStatus.Unreadable) return new LogView(LogViewStatus.Unreadable);
        if (records is null)
        {
            var tail = human.FinishFile(unfinished);
            if (tail is not null) progress(tail);
        }
        return new LogView(LogViewStatus.Found, records, malformed, unfinished);
    }
}
