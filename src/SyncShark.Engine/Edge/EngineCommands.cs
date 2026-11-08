using System.Globalization;
using Massdex.SyncShark.Engine.Edge.Results;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Maps one engine invocation to EngineOperations and a result record (section 9.2): one method per
/// EngineCommandKey. Option values are read only through EngineCliCatalog.Flag(EngineOptionKey); validation already
/// ran in EngineCliCatalog.Validate.</summary>
public sealed class EngineCommands(EngineOperations operations)
{
    private readonly EngineOperations _operations = operations;

    /// <summary>Dispatches by EngineCliCatalog.CommandKey(invocation.Command).</summary>
    public async Task<object> RunAsync(SyncCliInvocation invocation, CancellationToken ct) =>
        EngineCliCatalog.CommandKey(invocation.Command) switch
        {
            EngineCommandKey.PairsList => PairsList(),
            EngineCommandKey.PairsAdd => await PairsAddAsync(invocation, ct),
            EngineCommandKey.PairsImport => await PairsImportAsync(invocation, ct),
            EngineCommandKey.PairsRemove => await PairsRemoveAsync(invocation, ct),
            EngineCommandKey.PairsEnable => await PairsEnableAsync(invocation, ct),
            EngineCommandKey.PairsDisable => await PairsDisableAsync(invocation, ct),
            EngineCommandKey.PairsExclude => await PairsExcludeAsync(invocation, ct),
            EngineCommandKey.PairsInclude => await PairsIncludeAsync(invocation, ct),
            EngineCommandKey.PairsBind => await PairsBindAsync(invocation, ct),
            EngineCommandKey.Repair => await RepairAsync(invocation, ct),
            EngineCommandKey.Logs => Logs(invocation, ct),
            EngineCommandKey.Status => Status(invocation),
            EngineCommandKey.Issues => Issues(invocation),
            EngineCommandKey.Resolve => await ResolveAsync(invocation, ct),
            EngineCommandKey.Recovery => Recovery(invocation),
            EngineCommandKey.RecoveryResolve => await RecoveryResolveAsync(invocation, ct),
            EngineCommandKey.IgnoresList => IgnoresList(invocation),
            EngineCommandKey.IgnoresSet => await IgnoresSetAsync(invocation, ct),
            EngineCommandKey.SettingsList => SettingsList(),
            EngineCommandKey.SettingsSet => SettingsSet(invocation),
            EngineCommandKey.PreferencesShow => await PreferencesShowAsync(ct),
            EngineCommandKey.PreferencesSet => await PreferencesSetAsync(invocation, ct),
            EngineCommandKey.HostStart => HostResultOf(await _operations.HostStartAsync(ct)),
            EngineCommandKey.HostStatus => HostResultOf(await _operations.HostStatusAsync(ct)),
            EngineCommandKey.HostPause => HostResultOf(await _operations.HostPauseAsync(
                invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Pair)), ct)),
            EngineCommandKey.HostResume => HostResultOf(await _operations.HostResumeAsync(
                invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Pair)), ct)),
            EngineCommandKey.HostStop => HostResultOf(await _operations.HostStopAsync(ct)),
            _ => throw new System.Diagnostics.UnreachableException(),
        };

    private PairsResult PairsList() =>
        new(_operations.ListPairs().Select(View).ToArray());

    private async Task<PairResult> PairsAddAsync(SyncCliInvocation invocation, CancellationToken ct) =>
        new(View(await _operations.AddPairAsync(Value(invocation, EngineOptionKey.Name),
            Value(invocation, EngineOptionKey.Local), Value(invocation, EngineOptionKey.Path),
            Value(invocation, EngineOptionKey.Account), FirstRun(invocation), ct)));

    private async Task<PairImportResult> PairsImportAsync(SyncCliInvocation invocation, CancellationToken ct)
    {
        var imported = await _operations.ImportPairAsync(Value(invocation, EngineOptionKey.Pair), ct);
        return new(View(imported.Pair), imported.Counts);
    }

    private async Task<DoneResult> PairsRemoveAsync(SyncCliInvocation invocation, CancellationToken ct)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        await _operations.RemovePairAsync(pairId, ct);
        return new(Text.Get(TextKey.ResultPairRemoved, (TextArg.Pair, pairId)));
    }

    private async Task<PairResult> PairsEnableAsync(SyncCliInvocation invocation, CancellationToken ct) =>
        new(View(await _operations.SetEnabledAsync(Value(invocation, EngineOptionKey.Pair), true, ct)));

    private async Task<PairResult> PairsDisableAsync(SyncCliInvocation invocation, CancellationToken ct) =>
        new(View(await _operations.SetEnabledAsync(Value(invocation, EngineOptionKey.Pair), false, ct)));

    private async Task<ExcludeResult> PairsExcludeAsync(SyncCliInvocation invocation, CancellationToken ct)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        var exclusion = ExclusionOf(invocation, Confirmed(invocation, EngineOptionKey.Keep));
        return Confirmed(invocation, EngineOptionKey.Yes)
            ? new ExcludeResult(View(await _operations.ExcludeAsync(pairId, exclusion, ct)), true, null)
            : new ExcludeResult(PairViewOf(pairId), false,
                await _operations.ExcludePreviewAsync(pairId, exclusion, ct));
    }

    private async Task<PairResult> PairsIncludeAsync(SyncCliInvocation invocation, CancellationToken ct) =>
        new(View(await _operations.IncludeAsync(Value(invocation, EngineOptionKey.Pair),
            Parse<Side>(Value(invocation, EngineOptionKey.Side)), Value(invocation, EngineOptionKey.Rel), ct)));

    // Only the committed BindRootsAsync state constructs the success result; a failed or cancelled bind returns no
    // result at all (section 9.3).
    private async Task<RootBindingResult> PairsBindAsync(SyncCliInvocation invocation, CancellationToken ct)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        await _operations.BindRootsAsync(pairId, ct);
        return new RootBindingResult(pairId);
    }

    private async Task<RepairResult> RepairAsync(SyncCliInvocation invocation, CancellationToken ct) =>
        new RepairResult(await _operations.RepairAsync(Value(invocation, EngineOptionKey.Pair), ct,
            Confirmed(invocation, EngineOptionKey.ShowFiles)));

    // Read-only view of one stored run log through the shared human projection; no store, lock or provider access.
    // Human blocks stream to the caller's output transport; structured output keeps the six canonical fields.
    private LogsResult Logs(SyncCliInvocation invocation, CancellationToken ct)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        var runId = Value(invocation, EngineOptionKey.Run);
        return new LogsResult(pairId, runId,
            _operations.ReadLogView(pairId, runId, Confirmed(invocation, EngineOptionKey.ShowFiles),
                invocation.Json, ct));
    }

    private StatusResult Status(SyncCliInvocation invocation) =>
        new(_operations.Status(invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Pair))));

    private IssuesResult Issues(SyncCliInvocation invocation)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        var kind = invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Kind));
        var issues = _operations.Issues(pairId, kind is null ? null : Parse<IssueKind>(kind),
            Confirmed(invocation, EngineOptionKey.Ignored));
        // One page-batched Run projection; a missing entry is rendered Unavailable by the result owner.
        return IssuesResult.From(pairId, issues) with
        {
            RetryViews = issues.Count == 0
                ? null
                : _operations.ReadIssueRetryViews(pairId, issues.Select(issue => issue.Id).ToArray()),
        };
    }

    private async Task<object> ResolveAsync(SyncCliInvocation invocation, CancellationToken ct)
    {
        var issueValue = invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Issue));
        var all = Confirmed(invocation, EngineOptionKey.All);
        var reviewed = invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Reviewed));
        if (issueValue is not null && string.IsNullOrWhiteSpace(reviewed) || all && reviewed is not null)
            throw new SyncCliUsageException(EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid));
        var choiceText = Value(invocation, EngineOptionKey.Choice);
        // The catalogue lists one choice vocabulary for both commands; the recovery choices belong only to
        // recovery-resolve and are refused here before any state access.
        if (!TryParse<ResolutionChoice>(choiceText, out var choice) || TryParse<RecoveryChoice>(choiceText, out _))
            throw new SyncCliUsageException(EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid));
        var firstRun = FirstRun(invocation);
        var newName = invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Name));
        var rules = invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Rules));
        // Rule bodies themselves are normalized and validated once by the Run owner.
        if (!EngineOperations.RequestValid(issueValue is not null, all, choice, newName is not null, firstRun is not null, rules))
            throw new SyncCliUsageException(EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid));
        long? issueId = null;
        if (issueValue is not null)
        {
            if (!long.TryParse(issueValue, out var parsedIssue))
                throw new SyncCliUsageException(EngineCliCatalog.Message(EngineMessageKey.BadNumber,
                    (EngineMessageArg.Flag, EngineCliCatalog.Flag(EngineOptionKey.Issue))));
            issueId = parsedIssue;
        }
        var result = await _operations.ResolveAsync(Value(invocation, EngineOptionKey.Pair), issueId,
            choice, newName, firstRun, all, rules, ct, reviewed);
        if (all) return new RetryAllResult(result.Retried, result.Succeeded);
        return result.Repair is not null
            ? new RepairResult(result.Repair)
            : new ResolveResult(issueId!.Value, choice, result.Resolved, result.Outcome);
    }

    // Without --operation this lists active operations from --after-operation; with it, the exact operation's
    // summary or its member page from --after. The Run views carry Items/LastOrdinal/HasMore unchanged, so the
    // continuation is complete and no page is truncated here.
    private object Recovery(SyncCliInvocation invocation)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        var operation = invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.Operation));
        if (operation is null)
            return _operations.RecoveryList(pairId,
                invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.AfterOperation)));
        var after = OptionalNumber(invocation, EngineOptionKey.After);
        return after is null
            ? _operations.Recovery(pairId, operation)
            : _operations.RecoveryItems(pairId, operation, after.Value, _operations.PageTake);
    }

    private async Task<RemovalResult> RecoveryResolveAsync(SyncCliInvocation invocation, CancellationToken ct)
    {
        if (!TryParse<RecoveryChoice>(Value(invocation, EngineOptionKey.Choice), out var choice))
            throw new SyncCliUsageException(EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid));
        return await _operations.ResolveRecoveryAsync(Value(invocation, EngineOptionKey.Pair),
            Value(invocation, EngineOptionKey.Operation), invocation.Generation!.Value, choice,
            Confirmed(invocation, EngineOptionKey.Yes), OptionalNumber(invocation, EngineOptionKey.Item),
            OptionalNumber(invocation, EngineOptionKey.ItemGeneration), ct);
    }

    private IgnoresResult IgnoresList(SyncCliInvocation invocation)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        var folderKeep = Confirmed(invocation, EngineOptionKey.FolderKeep);
        return new IgnoresResult(pairId, EngineOperations.IgnoreRules(_operations.Pair(pairId), folderKeep),
            folderKeep);
    }

    private async Task<IgnoresResult> IgnoresSetAsync(SyncCliInvocation invocation, CancellationToken ct)
    {
        var pairId = Value(invocation, EngineOptionKey.Pair);
        var folderKeep = Confirmed(invocation, EngineOptionKey.FolderKeep);
        var pair = await _operations.SetIgnoreRulesAsync(pairId, Value(invocation, EngineOptionKey.Rules), folderKeep, ct);
        return new IgnoresResult(pairId, EngineOperations.IgnoreRules(pair, folderKeep), folderKeep);
    }

    private SettingsResult SettingsList() =>
        SettingsResultOf(_operations.Settings());

    private SettingsResult SettingsSet(SyncCliInvocation invocation) =>
        SettingsResultOf(_operations.SetSetting(Value(invocation, EngineOptionKey.Name),
            Value(invocation, EngineOptionKey.Value)));

    // The typed host-bound read/update; the shared UiPreferencesUpdate parser and merge stay the one owner, and
    // neither call lists pairs, drains configuration or touches a provider.
    private async Task<UiPreferencesResult> PreferencesShowAsync(CancellationToken ct) =>
        new(await _operations.ReadUiPreferencesAsync(ct));

    private async Task<UiPreferencesResult> PreferencesSetAsync(SyncCliInvocation invocation, CancellationToken ct) =>
        new(await _operations.UpdateUiPreferencesAsync(
            UiPreferencesUpdate.Parse(Value(invocation, EngineOptionKey.Settings)), ct));

    /// <summary>Builds the host result from the state the host actually reported; Edge alone owns the displayed
    /// text. The in-process host dispatcher calls this same method so CLI and UI text cannot diverge.</summary>
    internal static HostResult HostResultOf((AutoStatus? Status, string? Detail) hostStatus) =>
        new(IssuePresentation.HostStatusKey(hostStatus.Status).ToString(),
            IssuePresentation.RenderHostStatus(hostStatus.Status, hostStatus.Detail, null));

    private static string Value(SyncCliInvocation invocation, EngineOptionKey option) =>
        invocation.Value(EngineCliCatalog.Flag(option))!;

    private static bool Confirmed(SyncCliInvocation invocation, EngineOptionKey option) =>
        invocation.Flag(EngineCliCatalog.Flag(option));

    private static FirstRunPolicy? FirstRun(SyncCliInvocation invocation) =>
        invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.FirstRun)) is { } value ? Parse<FirstRunPolicy>(value) : null;

    private static long? OptionalNumber(SyncCliInvocation invocation, EngineOptionKey option) =>
        invocation.Value(EngineCliCatalog.Flag(option)) is { } value
            ? long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;

    private static Exclusion ExclusionOf(SyncCliInvocation invocation, bool keep) =>
        new(Parse<Side>(Value(invocation, EngineOptionKey.Side)), Value(invocation, EngineOptionKey.Rel), keep);

    // The catalog's listed value is the enum name with the separator removed (name-clash, newest-wins, ...),
    // so every non-alphanumeric is dropped. A declared member is required: an unknown or numeric spelling fails.
    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.Parse<T>(new string(value.Where(char.IsLetterOrDigit).ToArray()), ignoreCase: true);

    private static bool TryParse<T>(string value, out T parsed) where T : struct, Enum =>
        EngineCliCatalog.TryParseValue(value, out parsed);

    private static PairView View(PairConfig pair) => new(pair.PairId, pair.Name, pair.LocalRoot, pair.DropboxRoot,
        pair.ConnectionId, pair.FirstRun, pair.Enabled, pair.Exclusions, pair.Roots is not null);

    private PairView PairViewOf(string pairId) => View(_operations.Pair(pairId));

    private static SettingsResult SettingsResultOf(
        IReadOnlyList<(string Name, string Value, bool Overridden)> settings) =>
        new(settings.Select(setting => new SettingView(setting.Name, setting.Value, setting.Overridden)).ToArray());
}
