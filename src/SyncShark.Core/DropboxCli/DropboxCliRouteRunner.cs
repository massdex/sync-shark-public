using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

/// <summary>The one call path every Dropbox handler uses: open, approve, call and poll a batch.</summary>
public sealed class DropboxCliRouteRunner(DropboxAccountClient account, DropboxCliArguments arguments,
    DropboxCliSettings settings, Action<string> progress)
{
    /// <summary>Opens the exact account and optional namespace, or fails with the missing-target code.</summary>
    public static async Task<DropboxAccountClient> OpenAsync(IDropboxCliAccess access, DropboxCliArguments arguments,
        CancellationToken cancellationToken)
    {
        var accountId = arguments.Value(DropboxCliOption.Account)
            ?? throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.MissingRequired,
                ("command", arguments.Command), ("options", DropboxCliCatalog.Flag(DropboxCliOption.Account))));
        var client = await access.DropboxAccounts.OpenAsync(accountId, arguments.NamespaceId, cancellationToken);
        return client ?? throw new SyncCliTargetException(DropboxCliCatalog.Message(
            DropboxCliMessage.AccountNotFound, ("account", accountId)));
    }

    /// <summary>Approves, scope-checks, warns and calls one route through the shared client.</summary>
    public async Task<DropboxRouteResult> CallAsync(DropboxRouteCall call, CancellationToken cancellationToken)
    {
        DropboxCliSafety.RequireApproval(call.Route, arguments.Yes, arguments.Command);
        DropboxCliSafety.RequireScope(call.Route, account.Connection);
        if (call.Route.Deprecated)
            progress(DropboxCliCatalog.Message(DropboxCliMessage.Deprecated, ("route", call.Route.Name)));
        if (call.Route.Preview)
            progress(DropboxCliCatalog.Message(DropboxCliMessage.Preview, ("route", call.Route.Name)));
        return await account.Client.CallRouteAsync(call, settings.Retry, cancellationToken);
    }

    /// <summary>Reports one informational note on the progress channel, formatted from the catalog.</summary>
    public void Note(DropboxCliMessage key, params (string Name, string Value)[] values) =>
        progress(DropboxCliCatalog.Message(key, values));

    /// <summary>Polls one launched batch job until it completes, is still in progress, or has failed.</summary>
    public async Task<DropboxCliBatchData> PollBatchAsync(DropboxCliCatalog.PollDefinition poll, string jobId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < settings.BatchPollMaxWait)
        {
            if (settings.BatchPollInterval > TimeSpan.Zero)
                await Task.Delay(settings.BatchPollInterval, cancellationToken);
            var body = new JsonObject { [poll.JobField] = jobId }.ToJsonString();
            using var check = await CallAsync(
                new DropboxRouteCall(poll.Route, body, arguments.NamespaceId), cancellationToken);
            var root = check.Result.RootElement;
            // Only an explicit complete completes a job; a failed job is an error and an unknown tag is refused,
            // so a non-terminal or unrecognised status is never mistaken for success.
            switch (Tag(root))
            {
                case "in_progress":
                    continue;
                case "complete":
                    return new DropboxCliBatchData(true, null, BatchEntries(root));
                case "failed":
                    throw new SyncCliException("batch_failed",
                        DropboxCliCatalog.Message(DropboxCliMessage.BatchFailed, ("error", Failure(root))),
                        SyncCliExitCode.Failed);
                default:
                    throw new SyncCliException("batch_failed",
                        DropboxCliCatalog.Message(DropboxCliMessage.BatchStatusUnknown, ("route", poll.Route.Name),
                            ("tag", Tag(root) ?? "none")), SyncCliExitCode.Failed);
            }
        }
        return new DropboxCliBatchData(false, jobId, []);
    }

    // The failed variant's own payload, sanitized; a job-level status is never a raw provider error body.
    private static string Failure(JsonElement root) =>
        SyncActivityText.Safe(root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("failed", out var failed) ? failed.GetRawText() : root.GetRawText());

    private static string? Tag(JsonElement element) =>
        DropboxCliOutput.Str(element, ".tag") ?? DropboxCliOutput.Str(element, "tag");

    internal static IReadOnlyList<DropboxCliBatchEntry> BatchEntries(JsonElement root)
    {
        var entries = new List<DropboxCliBatchEntry>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var array) ||
            array.ValueKind != JsonValueKind.Array) return entries;
        foreach (var item in array.EnumerateArray())
        {
            // Dropbox's batch result entry is a union keyed by its variant: {"success":{"metadata":{...}}}
            // or {"failure":{...}}. The metadata is read from inside the success variant, not the entry root.
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("success", out var success))
            {
                var metadata = success.ValueKind == JsonValueKind.Object &&
                    success.TryGetProperty("metadata", out var nested) ? nested : success;
                entries.Add(new DropboxCliBatchEntry(true, DropboxCliOutput.Entry(metadata), null));
                continue;
            }
            entries.Add(new DropboxCliBatchEntry(false, null,
                item.ValueKind == JsonValueKind.Object && item.TryGetProperty("failure", out var failure)
                    ? failure.Clone() : null));
        }
        return entries;
    }

    /// <summary>True when a launched batch result already completed, returning its entries.</summary>
    internal static bool TryCompletedBatch(JsonElement root, out DropboxCliBatchData data)
    {
        data = null!;
        if (Tag(root) != "complete") return false;
        data = new DropboxCliBatchData(true, null, BatchEntries(root));
        return true;
    }
}
