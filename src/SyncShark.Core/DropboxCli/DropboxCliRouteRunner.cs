using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

/// <summary>The one call path every Dropbox handler uses: open, approve, call and poll a batch.</summary>
public sealed class DropboxCliRouteRunner(DropboxAccountClient account, DropboxCliArguments arguments,
    DropboxCliSettings settings, Action<string> progress, string resultFolder)
{
    private DropboxCliWriteReceipt? receipt;

    private void SaveReceipt(DropboxCliWriteReceipt value)
    {
        receipt = value;
        Directory.CreateDirectory(Path.GetDirectoryName(value.ReceiptPath)!);
        var temporary = value.ReceiptPath + ".writing";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, value.ReceiptPath, overwrite: true);
    }

    private DropboxCliWriteReceipt NewReceipt(string route) => new(
        Path.Combine(resultFolder, "pending", Guid.NewGuid().ToString("N") + ".json"),
        arguments.Command, account.Connection.ConnectionId, arguments.NamespaceId, route, "effect_unverified",
        null, null, "Verify the exact target before retrying. Never replay an uncertain write automatically.")
        { Path = arguments.Value(DropboxCliOption.Path), Destination = arguments.Value(DropboxCliOption.To) };

    private void ResolveReceipt(string status)
    {
        if (receipt is null) return;
        // Publish the known outcome first, so cleanup failure cannot leave false uncertainty.
        receipt = receipt with { Status = status };
        try
        {
            SaveReceipt(receipt);
            File.Delete(receipt.ReceiptPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            progress("Dropbox outcome is " + status + "; receipt cleanup needs retry: " + receipt.ReceiptPath + ". " + error.Message);
        }
    }

    private SyncCliException Unverified(Exception error)
    {
        var value = receipt!;
        return new SyncCliException("effect_uncertain", "Dropbox completion is unverified. " + value.Guidance +
            " Receipt: " + value.ReceiptPath +
            (value.JobId is null ? "" : " Job: " + value.JobId), SyncCliExitCode.Failed, error, value);
    }

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
    public async Task<DropboxRouteResult> CallAsync(DropboxRouteCall call, CancellationToken cancellationToken,
        bool completesWrite = true)
    {
        DropboxCliSafety.RequireApproval(call.Route, arguments.Yes, arguments.Command);
        DropboxCliSafety.RequireScope(call.Route, account.Connection);
        if (call.Route.Deprecated)
            progress(DropboxCliCatalog.Message(DropboxCliMessage.Deprecated, ("route", call.Route.Name)));
        if (call.Route.Preview)
            progress(DropboxCliCatalog.Message(DropboxCliMessage.Preview, ("route", call.Route.Name)));
        if (call.Route.Policy != DropboxRoutePolicy.Write)
            return await account.Client.CallRouteAsync(call, settings.Retry, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Durable admission precedes the effect. Cancellation never cancels receipt persistence.
        if (receipt is null || !receipt.EffectUncertain)
        {
            SaveReceipt(NewReceipt(call.Route.Name));
            progress("Dropbox write receipt: " + receipt!.ReceiptPath);
        }
        DropboxRouteResult? result = null;
        try
        {
            result = await account.Client.CallRouteAsync(call, settings.Retry, cancellationToken);
            var jobId = DropboxCliOutput.Str(result.Result.RootElement, "async_job_id");
            if (jobId is not null) SaveReceipt(receipt! with { Status = "job_pending", JobId = jobId });
            else if (completesWrite) ResolveReceipt("response_received");
            return result;
        }
        catch (Exception error)
        {
            result?.Dispose();
            // The pre-effect receipt deliberately remains unresolved if the response cannot be proven.
            if (error is DropboxRouteException && result is null)
            {
                ResolveReceipt("failed");
                throw;
            }
            throw Unverified(error);
        }
    }

    internal Exception PreservePendingWrite(Exception error) =>
        receipt?.EffectUncertain == true && error is not SyncCliException ? Unverified(error) : error;

    /// <summary>Reports one informational note on the progress channel, formatted from the catalog.</summary>
    public void Note(DropboxCliMessage key, params (string Name, string Value)[] values) =>
        progress(DropboxCliCatalog.Message(key, values));

    /// <summary>Polls one launched batch job until it completes, is still in progress, or has failed.</summary>
    public async Task<DropboxCliBatchData> PollBatchAsync(DropboxCliCatalog.PollDefinition poll, string jobId,
        CancellationToken cancellationToken)
    {
        var pending = (receipt ?? NewReceipt(poll.Route.Name)) with
        {
            Status = "job_pending", JobId = jobId, PollRoute = poll.Route.Name,
            Guidance = "Resume verification with dropbox api --route " + poll.Route.Name +
                " with the recorded account and namespace and --body " +
                new JsonObject { [poll.JobField] = jobId }.ToJsonString() +
                ". Quote JSON for your shell. Do not repeat the original write."
        };
        try
        {
        SaveReceipt(pending);
        var started = Stopwatch.StartNew();
        while (started.Elapsed < settings.BatchPollMaxWait)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    ResolveReceipt("complete");
                    return new DropboxCliBatchData(true, null, BatchEntries(root));
                case "failed":
                    ResolveReceipt("failed");
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
        catch (Exception error)
        {
            receipt ??= pending;
            if (error is SyncCliException { Code: "batch_failed" } cli)
                throw new SyncCliException(cli.Code, cli.Message, cli.ExitCode, cli, receipt);
            throw Unverified(error);
        }
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
