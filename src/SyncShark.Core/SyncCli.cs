using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

// The CLI pilot is a same-process adapter over the existing Sync Shark state authority.
// It never starts a second sync engine, never opens a new listener and never reads a
// protected credential; state-bearing commands hold the existing exclusive product lock.
public enum SyncCliExitCode
{
    Success = 0,
    Usage = 2,
    Busy = 3,
    MissingTarget = 4,
    NeedsReview = 5,
    Failed = 6,
    TransientUnavailable = 7,
    PendingVerification = 8,
    Canceled = 130
}

public class SyncCliException(string code, string message, SyncCliExitCode exitCode, Exception? inner = null,
    object? data = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public SyncCliExitCode ExitCode { get; } = exitCode;
    // A bounded refusal may carry its compact structured receipt, so --json still returns the exact
    // outcome, effect-attempted flag, stage times and committed identity instead of only a message.
    public object? ResultData { get; } = data;
    public int? RetryAfterSeconds { get; init; }
}

public sealed class SyncCliUsageException(string message)
    : SyncCliException("usage", message, SyncCliExitCode.Usage);

public sealed class SyncCliBusyException(string message, Exception? inner = null)
    : SyncCliException("busy", message, SyncCliExitCode.Busy, inner);

public sealed class SyncCliTargetException(string message)
    : SyncCliException("missing_target", message, SyncCliExitCode.MissingTarget);

public sealed class SyncCliAmbiguousTargetException(string message)
    : SyncCliException("ambiguous_target", message, SyncCliExitCode.MissingTarget);

public sealed class SyncCliReviewException(string message, object? data = null)
    : SyncCliException("review_required", message, SyncCliExitCode.NeedsReview, data: data);

public sealed class SyncCliUnsupportedException(string message)
    : SyncCliException("unsupported", message, SyncCliExitCode.NeedsReview);

public sealed class SyncCliOperationException(string message, Exception? inner = null, object? data = null)
    : SyncCliException("operation_failed", message, SyncCliExitCode.Failed, inner, data);

// The exact-file upload refusal or unproven effect. It uses one of the existing exit codes and carries the
// same compact receipt the success path returns, so --json never loses the outcome, the effect-attempted
// flag, the stage times or the committed identity.
public sealed class SyncCliExactFileUploadException(string code, string message, SyncCliExitCode exitCode,
    object receipt) : SyncCliException(code, message, exitCode, null, receipt);

public sealed record SyncCliError(string Code, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Retryable = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds = null);

public sealed record SyncCliEnvelope(int SchemaVersion, string Command, bool Ok, object? Data, SyncCliError? Error);

public sealed record SyncCliStatusData(string Product, string Version, string SourceRevision, string StateAccess,
    string ProductRoot, bool AuthorityStateExists, int AccountCount, IReadOnlyList<SyncCliAccountData> Accounts,
    SyncCliPairTallyData Pairs, int PendingActionCount, DateTimeOffset? LastSuccessfulRunUtc, string GuiRequirement,
    string Mode = "direct", bool RunActive = false, bool RunPaused = false, int AttentionCount = 0);

public sealed record SyncCliPairTallyData(int Total, int Enabled, int Disabled, int NeedingAttention);

public sealed record SyncCliAccountData(string ConnectionId, string Provider, string AccountId, string DisplayName,
    string AccountType, bool IsPrimary, bool IsActive, bool IsPaused, int PairCount);

public sealed record SyncCliAccountListData(int Count, IReadOnlyList<SyncCliAccountData> Accounts);

public sealed record SyncCliPairData(string Id, string Name, string Provider, string ConnectionId, string LocalRoot,
    string DropboxRoot, bool Enabled, int Priority, string Direction, long PolicyRevision,
    bool? InitialReviewApproved, bool FirstSyncReviewRequired, bool FilesOnDemandEnabled, int SelectionRuleCount,
    int IgnoreRuleCount, bool IsSelected, bool PropagateDeletions = false,
    int? RecoveryRetentionDays = 30, SyncRemoteRecoveryRootBinding? RecoveryRoot = null,
    string? DefaultStorage = null, bool SelectionDefaultIncluded = true,
    IReadOnlyList<SyncSelectionRule>? SelectionRules = null, IReadOnlyList<SyncIgnoreRule>? IgnoreRules = null,
    IReadOnlyList<SyncDirectoryStorageRule>? DirectoryStorageRules = null,
    IReadOnlyList<SyncFileStorageRule>? FileStorageRules = null,
    IReadOnlyList<string>? PendingStoragePaths = null, string? DropboxFolderId = null,
    SyncEndpointDocument? EndpointDocument = null, bool IsRetired = false);

public sealed record SyncCliPairListData(int Count, IReadOnlyList<SyncCliPairData> Pairs);

public sealed record SyncCliActivityItemData(DateTimeOffset OccurredUtc, string PairId, string RelativePath,
    string Outcome, string ItemKind, string FileType, long? SizeBytes, string Reason, string? LocalRoot,
    string? DropboxRoot, string? EventId = null, long? Sequence = null,
    IReadOnlyList<string>? Categories = null, string Severity = "Information", string Entity = "Unknown",
    string? AccountId = null, string? RunId = null, string? OperationId = null,
    SyncEndpointContext? EndpointContext = null);

public sealed record SyncCliActivityListData(IReadOnlyList<SyncCliActivityItemData> Items, int Limit, int Offset,
    int Returned, bool HasMore, long? NextSequence = null, long? PreviousSequence = null,
    SyncActivityRetentionWindow? HistoryWindow = null);

public sealed record SyncCliAutomationData(bool Enabled, int IntervalMinutes, bool StartWithWindows);

public sealed record SyncCliTransferData(int MaximumConcurrentPairs, int UploadKiBPerSecond, int DownloadKiBPerSecond,
    string ResourceMode = "Auto", int MaximumConcurrentFiles = 0, int UploadBatchSize = 0,
    int UploadCommitConcurrency = 0, int DeleteBatchSize = 0);

public sealed record SyncCliInterfaceData(string Theme, bool KeepRunningWhenClosed, bool NotifyProblems,
    bool NotifyCompleted, bool NotifyTransfers, bool ExplorerIntegrationEnabled, bool AutomaticUpdateChecks,
    bool CompactRows = false, bool NotificationSound = false, string StatusIconStyle = "Combined");

public sealed record SyncCliSettingsData(SyncCliAutomationData Automation, SyncCliTransferData Transfer,
    SyncCliInterfaceData Interface, bool EffectsApplied = false, string? EffectsNote = null,
    SyncTransferResourceStatus? Resources = null, long RecoveryMaxBytes = 20L * 1024 * 1024 * 1024,
    string? DataLocation = null, bool RestartRequired = false);

public sealed record SyncCliPlanItemData(string RelativePath, string Group, string ItemKind, string FileType,
    string Recommendation, string Effect, long? LocalSize, long? DropboxSize, DateTimeOffset? LocalModifiedUtc,
    DateTimeOffset? DropboxModifiedUtc);

public sealed record SyncCliPlanData(string PairId, string PairName, bool FirstSyncReviewRequired,
    bool HasCompleteInventory, int TotalItems, int LocalOnlyItems, int DropboxOnlyItems, int ConflictItems,
    int Returned, bool HasMore, IReadOnlyList<SyncCliPlanItemData> Items)
{
    public int Limit { get; init; } = SyncCli.ActivityDefaultLimit;
    public int Offset { get; init; }
    public int LocalUpdatedItems { get; init; }
    public int DropboxUpdatedItems { get; init; }
    // Human output is rendered inside the authority process, including bridge requests. Keep
    // presentation context out of the established JSON contract and its compatibility counters.
    [JsonIgnore] public SyncEndpointContext? PresentationContext { get; init; }
}

public sealed record SyncCliRunData(string PairId, string PairName, int Uploaded, int Downloaded, int Unchanged,
    int Conflicts, int Blocked, bool Stopped)
{
    public string? RunId { get; init; }
    public string? Outcome { get; init; }
    public int Failed { get; init; }
    public int NotAttempted { get; init; }
    public int NeedsVerification { get; init; }
    /// <summary>Files removed on a side (core v2 keeps the run's per-kind counts, .</summary>
    public int Deleted { get; init; }
    /// <summary>Files moved on a side.</summary>
    public int Moved { get; init; }
    /// <summary>Core v2 per-run throughput measurements; absent when collection is disabled.</summary>
    public V2.RunDiagnostics? Diagnostics { get; init; }
}

/// <summary>
/// One plan-only export: the CSV path, its plan generation and the rows written per marker kind, including
/// sleeping retained intents that no notice names. Nothing was executed and no endpoint content was changed;
/// the preparation's scan cursors and retry bookkeeping were retained only inside the isolated data folder,
/// never in the live product state.
/// </summary>
public sealed record SyncCliPlanExportData(string PairId, string PairName, string ExportPath, long Generation,
    int PlannedActions, int DeferredActions, int SkippedActions, int HeldDeletions, int RecoveryPending,
    int Notices, int PendingIntents, int Rows)
{
    public int UnsupportedReconciles { get; init; }
    public bool ExactInitialPlan => UnsupportedReconciles == 0;
}

/// <summary>One root whose stored scan cursor was cleared, so the pair's next run re-reads it in full.</summary>
public sealed record SyncCliRescanRootData(string Side, string RootPath);

/// <summary>The roots a rescan cleared for one pair; the pair's next run scans each of them in full.</summary>
public sealed record SyncCliRescanData(string PairId, string PairName, IReadOnlyList<SyncCliRescanRootData> Roots)
{
    /// <summary>Recovery notes, such as an unreadable retained prepare that the rescan discarded.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record SyncCliHelpData(string Usage, string StateAccess, IReadOnlyList<string> Commands,
    IReadOnlyList<string> RequiresRunningApplication, IReadOnlyList<string> NotInThisPilot,
    IReadOnlyDictionary<string, string> ExitCodes, string Guideline);

public sealed record SyncCliVersionData(string Product, string Version, string SourceRevision, string StateAccess);

// Read-only in-process startup evidence. No database table or live provider query is used to infer readiness.
public sealed record SyncCliAppStatusData(string State, string Phase, string Detail,
    DateTimeOffset StartedUtc, DateTimeOffset LastProgressUtc, DateTimeOffset? CompletedUtc,
    bool InitializationComplete, double StageElapsedSeconds, int ProcessId, bool RunActive,
    bool RunPaused, string Version, string SourceRevision, string? Error);

// DIAG354-4: the safe, redacted result of the one shared in-app read-only diagnostic action. Every
// member is shareable: the check name is an ordinal pair / side / account label, the detail and
// freshness carry no email, display name, connection or native account id, credential reference,
// local path, user-chosen pair name or file name, and the report is the identical redacted support
// report the Diagnostics page previews, copies and saves.
public sealed record SyncCliDiagnosticsCheckData(string Check, string Outcome, string Detail, string Freshness);

public sealed record SyncCliDiagnosticsData(string CheckedUtc, string Report, int Total, int Passed,
    int Warnings, int Failed, int Skipped, bool NeedsAttention, IReadOnlyList<SyncCliDiagnosticsCheckData> Checks,
    string EvidenceScope);

// The runtime adapter the CLI dispatches into. Production uses SyncCliRuntime, which owns the
// existing exclusive product lock and the existing SyncAuthorityRuntimeHost; checks may supply a
// bounded fixture runtime that carries the same decisions.
public interface ISyncCliRuntime : IAsyncDisposable
{
    Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken);
    Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken);
    Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken);
    Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
        CancellationToken cancellationToken, SyncActivityQuery? query = null, string? search = null,
        string? filter = null, string? pairId = null, string? itemType = null, DateTimeOffset? sinceUtc = null);
    Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken);
    Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken,
        int limit = SyncCli.ActivityDefaultLimit, int offset = 0);
    Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress, CancellationToken cancellationToken);
}

public static class SyncCli
{
    public const int SchemaVersion = 1;
    public const string ExecutableName = "SyncShark.exe";
    public const int ActivityDefaultLimit = 50;
    public const int ActivityMaximumLimit = 200;

    // One process owns the product state. The CLI never
    // force-closes another Sync Shark process, never bypasses the lock, never reads credential
    // bytes, never edits the authority database directly and never starts a second runtime.
    public const string StateAccessNote = "When Sync Shark is running, commands execute in that application's " +
        "existing runtime and return its result without showing the window. Otherwise the CLI takes the existing " +
        "exclusive product lock. It never bypasses a lock or starts a competing runtime. An uncertain request " +
        "is not replayed; check status before retrying a mutation.";

    public static readonly IReadOnlyList<string> SupportedCommands =
        SyncCliRegistry.Rows.Select(row => row.Usage).ToArray();

    public static readonly IReadOnlyList<string> RunningApplicationCommands =
        SyncCliRegistry.Rows.Where(row => row.NeedsApplication).Select(row => row.Command).ToArray();

    public static readonly IReadOnlyList<string> NotInThisPilotCommands =
        SyncCliRegistry.Blocked.Select(item => item.Value).ToArray();

    public static IReadOnlyDictionary<string, string> ExitCodeHelp => SyncCliRegistry.ExitCodes;

    public static bool IsCliInvocation(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], "--cli", StringComparison.OrdinalIgnoreCase);

    public static string DescribeException(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return SyncActivityText.Failure(error);
    }

    public static string HelpText()
    {
        var text = new StringBuilder();
        text.AppendLine($"{ExecutableName} --cli <command> [options] [--json]");
        text.AppendLine();
        text.AppendLine("Commands");
        foreach (var row in SyncCliRegistry.Rows) text.AppendLine("  " + row.Usage);
        text.AppendLine();
        text.AppendLine("Options");
        text.AppendLine("  --pair <exact-pair-id>       exact retained sync pair id (never a name, index or prefix)");
        text.AppendLine("  --account <exact-id>         exact retained provider account id; required by account-specific actions");
        text.AppendLine($"  --limit <1..{ActivityMaximumLimit}>            maximum rows returned by a list command");
        text.AppendLine("  --offset <0..n>              list rows to skip");
        text.AppendLine("  --timeout-seconds <seconds>   optional scan/run/apply deadline, 0.001..86400; safe cancellation,");
        text.AppendLine("                               not a process kill; completed work and operation logs are retained");
        text.AppendLine("  --yes                        required by the mutating actions listed below; never implies approval of first sync, conflicts,");
        text.AppendLine("                               deletion, ownership or credential decisions");
        text.AppendLine("  --json                       exactly one UTF-8 JSON object on standard output");
        text.AppendLine();
        text.AppendLine("Interactive or long-running prerequisites");
        foreach (var row in SyncCliRegistry.Rows.Where(row => row.Approval != "none"))
            text.AppendLine($"  {row.Command}  ({row.Approval})");
        text.AppendLine();
        text.AppendLine("Live provider or OS verification required before these are proven");
        foreach (var row in SyncCliRegistry.Rows.Where(row => row.LiveVerificationRequired))
            text.AppendLine("  " + row.Usage);
        text.AppendLine();
        text.AppendLine("Exact blocked dependencies (not implemented, not faked)");
        foreach (var item in SyncCliRegistry.Blocked) text.AppendLine($"  {item.Key}: {item.Value}");
        text.AppendLine();
        text.AppendLine("Exit codes");
        foreach (var pair in ExitCodeHelp) text.AppendLine($"  {pair.Key}  {pair.Value}");
        text.AppendLine();
        text.AppendLine("State access");
        text.AppendLine("  " + StateAccessNote);
        text.Append("  help, version and argument validation never open product state or a file.");
        return text.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static async Task<int> ExecuteAsync(string[] args, TextWriter standardOutput, TextWriter standardError,
        Func<ISyncCliRuntime> runtimeFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(runtimeFactory);

        SyncCliInvocation invocation;
        try
        {
            invocation = SyncCliParser.Parse(args);
        }
        catch (SyncCliException error)
        {
            return WriteFailure(standardOutput, standardError, SyncCliParser.JsonRequested(args),
                SyncCliParser.CommandCandidate(args), error);
        }

        switch (invocation.Kind)
        {
            case SyncCliRequestKind.Help:
                return WriteSuccess(standardOutput, invocation, HelpData(), HelpText());
            case SyncCliRequestKind.Version:
                var version = VersionData();
                return WriteSuccess(standardOutput, invocation, version,
                    $"Sync Shark {version.Version}+{version.SourceRevision}");
            case SyncCliRequestKind.Capabilities:
                var capabilities = SyncCliRegistry.Capabilities();
                return WriteSuccess(standardOutput, invocation, capabilities,
                    SyncCliRegistry.RenderCapabilities(capabilities));
            case SyncCliRequestKind.Unsupported:
                return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                    new SyncCliUnsupportedException(invocation.Message ?? "This command is not supported by the CLI pilot."));
        }

        // The shared handler runs in the actual authority process, including app-bridge commands.
        // A deadline must cancel that work, not merely abandon the client waiting for its result.
        var timeoutText = invocation.Value("--timeout-seconds");
        using var deadline = timeoutText is null ? null : new CancellationTokenSource(
            TimeSpan.FromSeconds(double.Parse(timeoutText, CultureInfo.InvariantCulture)));
        using var linkedCancellation = deadline is null ? null :
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var commandCancellation = linkedCancellation?.Token ?? cancellationToken;
        SyncCliException CancellationError(object? retainedData = null) => deadline?.IsCancellationRequested == true
            ? new SyncCliException("timed_out", $"The command exceeded its {timeoutText}-second deadline. " +
                "Safe cancellation was requested; completed work and operation logs are retained. " +
                "Inspect the retained run before retrying. No automatic retry was started.", SyncCliExitCode.Canceled,
                data: retainedData)
            : new SyncCliException("canceled", "The command was canceled before it finished; " +
                "retained work is reconciled by the next ordinary run.", SyncCliExitCode.Canceled, data: retainedData);

        ISyncCliRuntime? runtime = null;
        try
        {
            commandCancellation.ThrowIfCancellationRequested();
            // DIAG354-4: a command whose registry requirement is a state-free refusal is answered by the
            // running application, never by a second runtime. When no trusted same-product application owns
            // the product, refuse through the existing NeedsApplication gate BEFORE the runtime factory runs,
            // so the refusal never opens, creates, locks or reads product state. Every other command keeps
            // its existing order, so its own usage validation still reports the usage error it always did.
            if (SyncCliRegistry.RequiresStateFreeApplicationRefusal(invocation.Command) &&
                !RunningApplicationIsPresent())
                return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                    new SyncCliUnsupportedException(SyncCliRegistry.NeedsApplicationRefusal));
            runtime = runtimeFactory();
            if (runtime is ISyncCliOperations operations)
                standardError.WriteLine(SyncCliRegistry.FormatProgress(
                    $"Execution runtime: {operations.Mode}; authority process {Environment.ProcessId}."));
            if (deadline is not null)
                standardError.WriteLine(SyncCliRegistry.FormatProgress($"Command deadline: {timeoutText} seconds; " +
                    "safe cancellation preserves completed work and logs."));
            var data = await DispatchAsync(runtime, invocation, standardError, commandCancellation);
            // Some cancellation paths return a stopped/partial result after draining admitted work.
            // That must not turn an expired performance test into a successful command.
            if (deadline?.IsCancellationRequested == true)
                throw CancellationError(data);
            deadline?.CancelAfter(Timeout.InfiniteTimeSpan);
            // Cleanup is part of success. Never print a successful result and then hide a failed
            // checkpoint/disposal in finally. Do not dispose a failed runtime a second time.
            var completedRuntime = runtime;
            runtime = null;
            await completedRuntime.DisposeAsync();
            return WriteSuccess(standardOutput, invocation, data, RenderHuman(invocation, data));
        }
        catch (SyncCliException error) when (deadline?.IsCancellationRequested == true)
        {
            return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                CancellationError(error.ResultData));
        }
        catch (SyncCliBusyException error) when (invocation.Command == "pairs remove")
        {
            return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                new SyncCliBusyException("Pair removal requires the exclusive product state lock. " +
                    "Close Sync Shark and retry. Nothing was removed.", error));
        }
        catch (SyncCliException error)
        {
            return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command, error);
        }
        catch (V2.PairPausedException paused)
        {
            return WriteSuccess(standardOutput, invocation,
                new { pairId = paused.PairId, outcome = V2.RunOutcomes.Paused }, "Pair paused; use pairs resume to continue.");
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                CancellationError());
        }
        catch (V2.SyncRunPendingVerificationException error)
        {
            return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                new SyncCliException("pending_verification", error.Message, SyncCliExitCode.PendingVerification, error)
                    { RetryAfterSeconds = error.RetryAfterSeconds });
        }
        catch (Exception error) when (ProviderTransientFailure.IsTransient(error, commandCancellation))
        {
            return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                new SyncCliException("transient_unavailable", DescribeException(error),
                    SyncCliExitCode.TransientUnavailable, error));
        }
        catch (Exception error)
        {
            return WriteFailure(standardOutput, standardError, invocation.Json, invocation.Command,
                new SyncCliOperationException(DescribeException(error)));
        }
        finally
        {
            if (runtime is not null)
            {
                try { await runtime.DisposeAsync(); }
                catch (Exception cleanupError)
                {
                    standardError.WriteLine("Sync Shark (cleanup_failed): " + DescribeException(cleanupError) +
                        ". Safe cleanup was not confirmed.");
                }
            }
        }
    }

    private static async Task<object> DispatchAsync(ISyncCliRuntime runtime, SyncCliInvocation invocation,
        TextWriter standardError, CancellationToken cancellationToken)
        => await SyncCliRegistry.DispatchAsync(runtime, invocation, standardError, cancellationToken);

    // help, version, capabilities and any argument-validation failure must not open product state or
    // start a runtime, so the caller can answer them without an application or a product lock.
    public static bool IsStateFreeInvocation(string[] args)
    {
        try
        {
            return SyncCliParser.Parse(args).Kind is SyncCliRequestKind.Help or SyncCliRequestKind.Version
                or SyncCliRequestKind.Capabilities;
        }
        catch (SyncCliException)
        {
            return true;
        }
    }

    // DIAG354-4: the existing same-product bridge discovery, used only to answer whether a command the
    // registry marks as needing the running application can be answered here. The running application
    // publishes one hidden receiver window under this exact title, and the existing transport verifies
    // that its owning process is this product, in this session, under this user's token. This very
    // process owning that window is the running application itself, which is exactly the bridge
    // dispatch path. A foreign window with the same title is not a running application, so the caller's
    // honest refusal follows. Nothing here reads, creates or locks product state.
    internal static bool RunningApplicationIsPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return SyncCliBridgeTransport.FindReceiver(includeCurrentProcess: true, out _) != IntPtr.Zero;
        }
        catch (Exception) { return false; }
    }

    private static SyncCliHelpData HelpData() => new($"{ExecutableName} --cli <command> [options] [--json]",
        "exclusive-product-lock", SupportedCommands, RunningApplicationCommands, NotInThisPilotCommands,
        ExitCodeHelp, StateAccessNote);

    private static SyncCliVersionData VersionData()
    {
        var informational = BuildIdentity.InformationalVersion;
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        var version = separator < 0 ? informational : informational[..separator];
        return new SyncCliVersionData("Sync Shark", version, BuildIdentity.SourceRevision,
            "exclusive-product-lock");
    }

    // Retain the conservative inner-JSON sample bound used by existing callers.
    // FitResponseItems below checks the actual unchanged nested UTF-16 transport.
    // This is paging/sample presentation only; retained outcomes are never truncated.
    internal static T[] BoundedResultItems<T>(IEnumerable<T> source, int limit)
    {
        var items = new List<T>();
        var bytes = 0;
        foreach (var item in source.Take(limit))
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions).Length + 1;
            if (bytes + size > 12 * 1024) break;
            items.Add(item); bytes += size;
        }
        return items.ToArray();
    }

    // Measure the actual nested bridge envelope, not just the inner UTF-8 JSON.
    // The unchanged COPYDATA boundary carries UTF-16 plus its null terminator.
    // Only an ordered presentation prefix is reduced; counts/identity stay in
    // create(), and every omitted row remains available in the retained store.
    internal static TData FitResponseItems<TItem, TData>(IReadOnlyList<TItem> source,
        Func<TItem[], TData> create, string command, bool allowEmptySample = false, int reserveCharacters = 0)
    {
        var empty = create([]);
        if (!ResponseFits(empty, command, reserveCharacters))
            throw new SyncCliOperationException("The retained response context exceeds the running-app limit. No identity or path was truncated.");
        var lower = 0;
        var upper = source.Count;
        while (lower < upper)
        {
            var count = lower + (upper - lower + 1) / 2;
            if (ResponseFits(create(source.Take(count).ToArray()), command, reserveCharacters)) lower = count;
            else upper = count - 1;
        }
        if (lower == 0 && source.Count > 0 && !allowEmptySample)
            throw new SyncCliOperationException("One retained row exceeds the running-app response limit. No path was truncated and no empty continuation page was returned.");
        return create(source.Take(lower).ToArray());
    }

    internal static bool ResponseFits(object? data, string command, int reserveCharacters = 0)
    {
        var json = JsonSerializer.Serialize(new SyncCliEnvelope(SchemaVersion, command, true, data, null),
            JsonOptions) + Environment.NewLine;
        return TextFits(json) && TextFits(RenderHuman(data).TrimEnd('\r', '\n') + Environment.NewLine);

        bool TextFits(string text)
        {
            var payload = SyncCliBridgeProtocol.EncodeReply(new SyncCliBridgeReply("result", new string('0', 32),
                true, 0, text, null));
            return payload.Length + reserveCharacters < SyncCliBridgeProtocol.MaximumPayloadBytes &&
                   Encoding.Unicode.GetByteCount(payload + "\0") <= SyncCliBridgeProtocol.MaximumPayloadBytes * 2;
        }
    }

    private static int WriteSuccess(TextWriter standardOutput, SyncCliInvocation invocation, object? data,
        string human)
    {
        if (invocation.Json)
        {
            WriteJson(standardOutput, new SyncCliEnvelope(SchemaVersion, invocation.Command, true, data, null));
            return (int)SyncCliExitCode.Success;
        }
        if (human.Length > 0) standardOutput.WriteLine(human.TrimEnd('\r', '\n'));
        return (int)SyncCliExitCode.Success;
    }

    private static int WriteFailure(TextWriter standardOutput, TextWriter standardError, bool json, string command,
        SyncCliException error)
    {
        var message = SyncActivityText.Safe(error.Message);
        if (message.Length == 0) message = error.Code;
        // Progress and errors are written to standard error so that standard output stays a single
        // machine-readable object, and so that a failure is visible even without --json.
        standardError.WriteLine("Sync Shark (" + error.Code + "): " + message);
        if (json)
            WriteJson(standardOutput, new SyncCliEnvelope(SchemaVersion, command, false, error.ResultData,
                new SyncCliError(error.Code, message,
                    error.ExitCode is SyncCliExitCode.TransientUnavailable or SyncCliExitCode.PendingVerification ? true : null,
                    error.RetryAfterSeconds)));
        return (int)error.ExitCode;
    }

    private static void WriteJson(TextWriter standardOutput, SyncCliEnvelope envelope) =>
        standardOutput.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));

    internal static string RenderHuman(SyncCliInvocation invocation, object? data) => RenderHuman(data);

    internal static string RenderHuman(object? data)
    {
        if (SyncCliRegistry.TryRender(data, out var registered)) return registered;
        var text = new StringBuilder();
        switch (data)
        {
            case SyncCliAppStatusData startup:
                text.AppendLine($"App startup: {startup.State}; phase: {startup.Phase}");
                text.AppendLine(startup.Detail);
                text.AppendLine($"Initialization and recovery complete: {(startup.InitializationComplete ? "yes" : "no")}");
                text.AppendLine($"Last real progress: {startup.LastProgressUtc:O}; stage elapsed: {startup.StageElapsedSeconds:F1}s");
                text.AppendLine($"Process: {startup.ProcessId}; version: {startup.Version}; source: {startup.SourceRevision}");
                text.AppendLine($"Run active: {startup.RunActive}; paused: {startup.RunPaused}");
                if (startup.Error is not null) text.AppendLine("Startup error: " + startup.Error);
                break;
            case SyncCliStatusData status:
                text.AppendLine($"Sync Shark {status.Version}+{status.SourceRevision}");
                text.AppendLine($"Product state: {status.ProductRoot}");
                text.AppendLine($"Authority bookkeeping present: {(status.AuthorityStateExists ? "yes" : "no")}");
                text.AppendLine($"Accounts: {status.AccountCount}");
                text.AppendLine($"Pairs: {status.Pairs.Total} total, {status.Pairs.Enabled} enabled, " +
                                $"{status.Pairs.Disabled} disabled, {status.Pairs.NeedingAttention} needing attention");
                text.AppendLine($"Pending retained actions: {status.PendingActionCount}");
                text.AppendLine("Last successful run: " + (status.LastSuccessfulRunUtc is { } last
                    ? last.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                    : "none retained"));
                foreach (var account in status.Accounts)
                    text.AppendLine($"  account {account.ConnectionId}  {account.Provider}  " +
                                    $"{(account.IsPrimary ? "primary" : "additional")}  pairs={account.PairCount}  " +
                                    $"paused={(account.IsPaused ? "yes" : "no")}  {account.DisplayName}");
                text.AppendLine("Note: " + status.GuiRequirement);
                break;
            case SyncCliAccountListData accounts:
                text.AppendLine($"{accounts.Count} account(s)");
                foreach (var account in accounts.Accounts)
                    text.AppendLine($"{account.ConnectionId}  {account.Provider}  " +
                                    $"{(account.IsPrimary ? "primary" : "additional")}  " +
                                    $"pairs={account.PairCount}  paused={(account.IsPaused ? "yes" : "no")}  " +
                                    $"{account.DisplayName}");
                break;
            case SyncCliPairListData pairs:
                text.AppendLine($"{pairs.Count} pair(s)");
                foreach (var pair in pairs.Pairs) text.AppendLine(RenderPairLine(pair));
                break;
            case SyncCliPairData pair:
                text.AppendLine(RenderPairLine(pair));
                text.AppendLine($"  direction={pair.Direction}  policyRevision={pair.PolicyRevision}");
                text.AppendLine($"  first sync review required: {(pair.FirstSyncReviewRequired ? "yes" : "no")}  " +
                                $"selected={(pair.IsSelected ? "yes" : "no")}  " +
                                $"files on demand={(pair.FilesOnDemandEnabled ? "on" : "off")}");
                text.AppendLine($"  selection rules: {pair.SelectionRuleCount}  ignore rules: {pair.IgnoreRuleCount}");
                text.AppendLine($"  propagate deletions={pair.PropagateDeletions}  recovery retention days={pair.RecoveryRetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "never"}  default storage={pair.DefaultStorage}");
                text.AppendLine($"  recovery root: {pair.RecoveryRoot?.RootPath ?? "unbound"}");
                text.AppendLine($"  selection default included={pair.SelectionDefaultIncluded}");
                foreach (var rule in pair.SelectionRules ?? []) text.AppendLine("  selection: " + JsonSerializer.Serialize(rule, JsonOptions));
                foreach (var rule in pair.IgnoreRules ?? []) text.AppendLine("  ignore: " + JsonSerializer.Serialize(rule, JsonOptions));
                foreach (var rule in pair.DirectoryStorageRules ?? []) text.AppendLine("  directory storage: " + JsonSerializer.Serialize(rule, JsonOptions));
                foreach (var rule in pair.FileStorageRules ?? []) text.AppendLine("  file storage: " + JsonSerializer.Serialize(rule, JsonOptions));
                foreach (var path in pair.PendingStoragePaths ?? []) text.AppendLine("  pending storage: " + path);
                break;
            case SyncCliActivityListData activity:
                if (activity.HistoryWindow is { } window) text.AppendLine(window.Describe());
                text.AppendLine($"{activity.Returned} row(s) from offset {activity.Offset} " +
                                $"(limit {activity.Limit}, more={(activity.HasMore ? "yes" : "no")})");
                foreach (var item in activity.Items)
                    text.AppendLine($"{item.OccurredUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}  " +
                                    $"{item.Outcome}  {item.RelativePath}  pair={item.PairId}  " +
                                    $"severity={item.Severity}  sequence={item.Sequence}  run={item.RunId}  operation={item.OperationId}\n{item.Reason}");
                if (activity.PreviousSequence is { } previousSequence)
                    text.AppendLine(activity.HasMore
                        ? $"Load earlier matching events with --log --order newest --before {previousSequence} and the same run/account filters."
                        : "No earlier matching events are retained.");
                if (activity.PreviousSequence is not null && activity.NextSequence is { } liveSequence)
                    text.AppendLine($"Follow future events with activity follow --seconds 1 --after {liveSequence} and the same run/account filters.");
                else if (activity.NextSequence is { } nextSequence)
                    text.AppendLine($"Continue this detailed log with --log --after {nextSequence} and the same run/account filters.");
                break;
            case SyncCliSettingsData settings:
                text.AppendLine($"Automatic syncing: {(settings.Automation.Enabled ? "enabled" : "disabled")}  " +
                                $"interval={settings.Automation.IntervalMinutes} minute(s)  " +
                                $"start with Windows={(settings.Automation.StartWithWindows ? "yes" : "no")}");
                text.AppendLine($"Transfers: concurrent pairs={settings.Transfer.MaximumConcurrentPairs}  " +
                                $"upload limit={DescribeLimit(settings.Transfer.UploadKiBPerSecond)}  " +
                                $"download limit={DescribeLimit(settings.Transfer.DownloadKiBPerSecond)}");
                text.AppendLine($"Transfer batches: upload batch size={DescribeAutomatic(settings.Transfer.UploadBatchSize)}  " +
                                $"upload commit concurrency={DescribeAutomatic(settings.Transfer.UploadCommitConcurrency)}  " +
                                $"delete batch size={DescribeAutomatic(settings.Transfer.DeleteBatchSize)}");
                text.AppendLine($"Interface: theme={settings.Interface.Theme}  " +
                                $"keep running when closed={(settings.Interface.KeepRunningWhenClosed ? "yes" : "no")}  " +
                                $"explorer integration={(settings.Interface.ExplorerIntegrationEnabled ? "enabled" : "disabled")}  " +
                                $"automatic update checks={(settings.Interface.AutomaticUpdateChecks ? "yes" : "no")}");
                text.AppendLine($"Compact rows={settings.Interface.CompactRows}  notification sound={settings.Interface.NotificationSound}");
                text.AppendLine($"Local recovery maximum bytes: {settings.RecoveryMaxBytes}");
                if (settings.EffectsNote is not null) text.AppendLine(settings.EffectsNote);
                break;
            case SyncCliPlanData plan:
                text.AppendLine($"Retained first-sync plan for '{plan.PairName}' ({plan.PairId})");
                text.AppendLine("First sync review required: " + (plan.FirstSyncReviewRequired ? "yes" : "no"));
                if (!plan.HasCompleteInventory)
                {
                    text.AppendLine("No complete retained inventory of both endpoints is available yet. " +
                                    "The CLI never refreshes an inventory; open Sync Shark to scan both sides.");
                    break;
                }
                var localTitle = PlanPortTitle(plan.PresentationContext, SyncAuthorityCloudSides.LogicalLeft);
                var remoteTitle = PlanPortTitle(plan.PresentationContext, SyncAuthorityCloudSides.LogicalRight);
                text.AppendLine($"Items: {plan.TotalItems}  only on {localTitle}={plan.LocalOnlyItems}  " +
                                $"only on {remoteTitle}={plan.DropboxOnlyItems}  " +
                                $"updated on {localTitle}={plan.LocalUpdatedItems}  " +
                                $"updated on {remoteTitle}={plan.DropboxUpdatedItems}  conflicts={plan.ConflictItems}");
                foreach (var item in plan.Items)
                {
                    text.AppendLine($"  {PlanAction(item.Recommendation, item.Group, localTitle, remoteTitle)}  {item.RelativePath}  " +
                                    $"({PlanGroup(item.Group, localTitle, remoteTitle)})");
                    if (!string.IsNullOrWhiteSpace(item.Effect)) text.AppendLine("    " + item.Effect);
                }
                text.AppendLine($"Returned {plan.Returned} row(s) from offset {plan.Offset}.");
                if (plan.HasMore) text.AppendLine($"Next page: plan --pair {plan.PairId} --limit {plan.Limit} --offset {plan.Offset + plan.Returned}");
                break;
            case SyncCliDiagnosticsData diagnostics:
                // DIAG354-4: the CLI prints the same redacted support report the Diagnostics page
                // previews, copies and saves, so the two surfaces can never disagree.
                text.Append(diagnostics.Report);
                break;
            case SyncCliRunData run:
                if (run.Outcome == V2.RunOutcomes.Paused) text.AppendLine("Pair paused; use pairs resume to continue from checkpoints.");
                text.AppendLine($"Pair '{run.PairName}' ({run.PairId}) finished: uploaded={run.Uploaded} " +
                                $"downloaded={run.Downloaded} deleted={run.Deleted} moved={run.Moved} " +
                                $"unchanged={run.Unchanged} conflicts={run.Conflicts} " +
                                $"blocked={run.Blocked} failed={run.Failed} not-attempted={run.NotAttempted} " +
                                $"needs-verification={run.NeedsVerification} stopped={(run.Stopped ? "yes" : "no")}");
                if (run.RunId is not null)
                    text.AppendLine($"Retained run: {run.RunId}. Detailed log: activity list --log --run {run.RunId}");
                break;
            case SyncCliPlanExportData export:
                text.AppendLine($"Plan-only export for '{export.PairName}' ({export.PairId}) generation " +
                                $"{export.Generation}: {export.PlannedActions} planned action(s), " +
                                $"{export.DeferredActions} deferred, {export.SkippedActions} skipped, " +
                                $"{export.HeldDeletions} held deletion(s), {export.RecoveryPending} recovery " +
                                $"pending, {export.Notices} notice(s), {export.PendingIntents} pending intent(s); " +
                                $"{export.Rows} CSV row(s).");
                text.AppendLine("CSV: " + export.ExportPath);
                text.AppendLine($"Unsupported read-only reconciliation: {export.UnsupportedReconciles}. " +
                    (export.ExactInitialPlan ? "This is the initial execution plan; deferred evidence may revise it." :
                        "This export is not an exact execution plan; inspect UNSUPPORTED-RECONCILE rows."));
                text.AppendLine("Nothing was executed and no endpoint content was changed. Planning bookkeeping " +
                                "(scan cursors and retries) was retained only in the isolated data folder.");
                break;
            case V2.DateRepairReport dates:
                text.AppendLine($"{(dates.CsvFile is null ? "Date repair" : "Date mismatch report")} {(dates.DryRun ? "read-only" : "complete")}: examined={dates.Examined} " +
                    $"eligible={dates.Eligible} changed={dates.Changed} skipped={dates.Skipped} " +
                    $"missing-write-history={dates.MissingWriteHistory} local-date-not-written={dates.LocalDateNotWritten}");
                if (dates.CsvFile is not null) text.AppendLine($"Mismatches: {dates.Mismatches}. CSV: {dates.CsvFile}");
                foreach (var example in dates.Examples)
                    text.AppendLine($"  {example.Path}: {example.OldUtc:O} -> {example.NewUtc:O} ({example.Outcome})");
                text.AppendLine($"Report: {dates.ReportFile}");
                break;
            case SyncCliRescanData rescan:
                text.AppendLine($"Pair '{rescan.PairName}' ({rescan.PairId}) will re-read {rescan.Roots.Count} " +
                                "root(s) in full on its next run; no file or baseline was changed:");
                foreach (var root in rescan.Roots) text.AppendLine($"  side {root.Side}: {root.RootPath}");
                foreach (var warning in rescan.Warnings) text.AppendLine("Warning: " + warning);
                break;
        }
        return text.ToString();
    }

    private static string RenderPairLine(SyncCliPairData pair) =>
        $"{pair.Id}  {pair.Name}  enabled={(pair.Enabled ? "yes" : "no")}  " +
        $"firstSyncReview={(pair.FirstSyncReviewRequired ? "required" : "not-required")}" +
        (pair.IsSelected ? "  selected=yes" : string.Empty) + Environment.NewLine + RenderEndpoints(pair);

    // C1: one shared renderer for the pair's ordered endpoints, used by both the list and show paths
    // through RenderPairLine and the pair detail block. The document is the only source of ordered
    // provider, root and account identity; the legacy fields are a fallback for pairs saved before it.
    private static string RenderEndpoints(SyncCliPairData pair)
    {
        var document = pair.EndpointDocument;
        if (document is null)
        {
            if (!SyncEndpoints.TryMapProvider(pair.Provider, out var kind) || kind == SyncEndpointKind.Local)
                return "  endpoints unavailable: unrecognized legacy provider";
            return $"  left: Local: {pair.LocalRoot}" + Environment.NewLine +
                   $"  right: {SyncEndpointPresentation.ShortLabel(kind)}: {pair.DropboxRoot}  account={DescribeConnection(pair.ConnectionId)}";
        }
        if (document.Version != SyncEndpointSchema.SymmetricEndpoints || document.Left is null || document.Right is null ||
            !SyncEndpointContracts.ValidateRecord(document.Left).IsValid ||
            !SyncEndpointContracts.ValidateRecord(document.Right).IsValid)
            return "  endpoints unavailable: invalid saved endpoint document (legacy fields not substituted)";
        // Display a saved, structurally valid pair without pretending to reverify remote ancestry.
        // Opaque Google folder ancestry needs the live binding path, not this read-only renderer.
        if (!SyncEndpoints.ValidatePair(SyncEndpointContracts.ToEndpoint(document.Left),
                SyncEndpointContracts.ToEndpoint(document.Right)).IsValid)
            return "  endpoints unavailable: invalid saved endpoint pair (legacy fields not substituted)";
        return $"  left: {DescribeEndpoint(document.Left)}" + Environment.NewLine +
               $"  right: {DescribeEndpoint(document.Right)}";
    }

    private static string DescribeEndpoint(SyncEndpointRecord endpoint) =>
        SyncEndpointPresentation.EndpointDisplay(endpoint) +
        (endpoint.IsLocal ? string.Empty : "  account=" + DescribeConnection(endpoint.ConnectionId ?? string.Empty));

    private static string PlanPortTitle(SyncEndpointContext? context, string port)
    {
        if (context is null) return port == SyncAuthorityCloudSides.LogicalLeft ? "inventory A" : "inventory B";
        var side = context.Left.CompatibilityPort == port ? context.Left : context.Right;
        return SyncEndpointPresentation.SectionTitle(side.Side, side.Kind);
    }

    private static string PlanAction(string action, string group, string local, string remote) => action switch
    {
        "Upload" when group == SyncBootstrapActions.LocalUpdatedGroup => "Update on " + remote,
        "Download" when group == SyncBootstrapActions.RemoteUpdatedGroup => "Update on " + local,
        "Upload" => "Copy to " + remote,
        "Download" => "Copy to " + local,
        "MoveLocal" => "Move on " + local,
        "MoveRemote" => "Move on " + remote,
        _ => action
    };

    private static string PlanGroup(string group, string local, string remote) => group switch
    {
        "Local only" => "Only on " + local,
        "Dropbox only" => "Only on " + remote,
        SyncBootstrapActions.LocalUpdatedGroup => "Changed since sync on " + local,
        SyncBootstrapActions.RemoteUpdatedGroup => "Changed since sync on " + remote,
        "Local newer" => "Newer on " + local,
        "Dropbox newer" => "Newer on " + remote,
        _ => SyncCliRegistry.ConflictGroupLabel(group)
    };

    private static string DescribeConnection(string connectionId) =>
        string.IsNullOrWhiteSpace(connectionId) ? "primary" : connectionId;

    private static string DescribeLimit(int kibibytesPerSecond) =>
        kibibytesPerSecond <= 0 ? "unlimited" : kibibytesPerSecond + " KiB/s";

    // Zero is "automatic": the engine uses its own default, so nothing overrides it in the product settings.
    private static string DescribeAutomatic(int value) => value <= 0 ? "automatic" : value.ToString();

    private static string Bound(string value, int maximum)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maximum ? text : text[..maximum] + "...";
    }
}

public enum SyncCliRequestKind
{
    Help,
    Version,
    Capabilities,
    State,
    Unsupported
}

public sealed record SyncCliInvocation(SyncCliRequestKind Kind, string Command, bool Json, string? PairId,
    string? AccountId, string? Path, string? Key, string? Search, string? Choice, string? Rules, string? Settings,
    string? Update, string? Selection, string? Availability, int Limit, int Offset, long? Generation, bool Yes,
    bool Copy, bool Download, bool UntilResumed, string? Message,
    IReadOnlyDictionary<string, string?>? Options = null, bool RequiresPair = false, int? Minutes = null,
    string? LocalRoot = null, string? Name = null, int? Priority = null, bool Enable = false,
    bool Disable = false, string? Description = null, string? Choices = null, bool AllowRecommended = false,
    IReadOnlyList<string>? Excludes = null, bool Verbose = false, IReadOnlyList<string>? ProvenanceLogs = null, IReadOnlyList<string>? ProvenancePrefixes = null)
{
    public string? Value(string name) =>
        Options is not null && Options.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) => Options is not null && Options.ContainsKey(name);
}

internal static class SyncCliParser
{
    internal static bool JsonRequested(string[] args)
    {
        for (var index = 1; index < args.Length; index++)
            if (string.Equals(args[index], "--json", StringComparison.Ordinal)) return true;
        return false;
    }

    internal static string CommandCandidate(string[] args)
    {
        var words = new List<string>();
        for (var index = 1; index < args.Length && words.Count < 2; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal)) break;
            words.Add(args[index]);
        }
        return string.Join(' ', words);
    }

    internal static SyncCliInvocation Parse(string[] args)
    {
        // One registry owns parsing, validation, help and the capability map so the direct path, the
        // running-application bridge and the published inventory cannot diverge.
        return SyncCliRegistry.Parse(args);
    }
}

internal static class SyncCliSanitize
{
    public const int MaximumMessageLength = 400;

    // Console output never carries a raw stack, a control character or an unbounded provider
    // message; protected values are never read in the first place.
    public static string? Message(string? value, int maximumLength = MaximumMessageLength)
    {
        if (maximumLength is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\t') builder.Append(' ');
            else if (!char.IsControl(character)) builder.Append(character);
            if (builder.Length >= maximumLength) break;
        }
        var text = builder.ToString().Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length == 0 ? null : text;
    }
}
