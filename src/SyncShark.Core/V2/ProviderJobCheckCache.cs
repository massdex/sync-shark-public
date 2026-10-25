using System.Collections.Concurrent;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Shares one provider job-status read among every entry that names the same job during a single reconcile pass.
/// An asynchronous provider job is one batch whose entries each carry their own identity; checking the job once per
/// entry repeats the same provider read hundreds of times. The first caller starts the read and every later caller of
/// that job id (for the owning provider client) awaits the same task, so the pass performs exactly one check per job.
/// The instance is created by the caller for one pass and discarded with it; nothing outlives the pass.
/// </summary>
public sealed class ProviderJobCheckCache
{
    private readonly ConcurrentDictionary<(object Owner, string Kind, string JobId), Lazy<Task<JsonElement>>> _checks = new();
    private readonly ConcurrentDictionary<(object Owner, string Kind, string JobId), int> _failures = new();
    private readonly ReconcileRunScope? _run = ReconcileRunScope.Current;

    /// <summary>
    /// The job status read for <paramref name="jobId"/>, performed by the first caller and shared by the rest.
    /// A failure is never remembered as a job outcome: the first one lets the next caller of this id retry the
    /// read once, and only a second consecutive failure is shared for the rest of the pass.
    /// </summary>
    public Task<JsonElement> ReadAsync(object owner, string kind, string jobId,
        Func<CancellationToken, Task<JsonElement>> read, CancellationToken ct)
    {
        var key = (owner, kind, jobId);
        ct.ThrowIfCancellationRequested();
        if (_run?.PendingJobs.TryGetValue(key, out var pending) == true) return Task.FromResult(pending);
        while (true)
        {
            if (_checks.TryGetValue(key, out var known)) return known.Value;
            // ExecutionAndPublication starts the read exactly once even when several entries ask at the same time;
            // a Lazy that loses the race below is dropped without ever running.
            var started = new Lazy<Task<JsonElement>>(
                () => ReadDroppingFailureAsync(key, read, ct), LazyThreadSafetyMode.ExecutionAndPublication);
            if (_checks.TryAdd(key, started)) return started.Value;
        }
    }

    private async Task<JsonElement> ReadDroppingFailureAsync((object, string, string) key,
        Func<CancellationToken, Task<JsonElement>> read, CancellationToken ct)
    {
        try
        {
            var result = await read(ct).ConfigureAwait(false);
            if (_run is not null && result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty(".tag", out var tag) && tag.GetString() == "in_progress")
            {
                // Do not retain an arbitrary provider body for the whole run.
                using var compact = JsonDocument.Parse("{\".tag\":\"in_progress\"}");
                _run.PendingJobs.TryAdd(key, compact.RootElement.Clone());
            }
            return result;
        }
        catch
        {
            // Not an outcome. The first failure lets the next caller of this id ask the provider again, exactly
            // once; a second failure stays cached for the rest of the pass so an unavailable provider is not
            // checked again once per entry.
            if (_failures.AddOrUpdate(key, 1, (_, count) => count + 1) == 1) _checks.TryRemove(key, out _);
            throw;
        }
    }
}
