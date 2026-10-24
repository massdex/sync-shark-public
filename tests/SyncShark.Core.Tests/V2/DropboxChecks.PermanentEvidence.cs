using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // D78: a terminal history failure answers Unknown, never deletion or restoration permission.
    // No disk or real retry sleeps: suitable for direct invocation before the public cursor fixture.
    private static async Task PermanentEvidenceCallbackChecksAsync()
    {
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("held.txt");
            h.Wire.RevisionResponseOverride = new
            {
                is_deleted = true, server_deleted = "invalid", entries = Array.Empty<object>()
            };
            var callbacks = new List<(string Path, SyncRemoteDeletionEvidence Evidence)>();
            var checkpoints = new List<DeletionEvidenceRecord>();
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "held.txt" };
            var result = await h.Client.LookupDeletionEvidenceForV2Async(["held.txt"], deleted, deleted,
                null, rows => checkpoints.AddRange(rows), CancellationToken.None,
                (path, evidence) => callbacks.Add((path, evidence)));
            Require(callbacks.Count == 1 && callbacks[0].Path == "held.txt" &&
                callbacks[0].Evidence == result["held.txt"],
                "permanent history failure answers exactly once and callback agrees with returned map");
            Require(result["held.txt"].LookupFailed && result["held.txt"].PermanentFailure &&
                result["held.txt"].DeletedUtc is null && result["held.txt"].ContentHash is null &&
                checkpoints.Count == 0 && h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 1,
                "terminal Unknown supplies no deletion/hash authority or durable success cache, with one probe");

            await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            callbacks.Clear();
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            await h.Inventory.ResolveDeletionEvidenceAsync(["held.txt"],
                (path, evidence) => callbacks.Add((path, evidence)), CancellationToken.None);
            Require(callbacks.Count == 1 && callbacks[0].Path == "held.txt" &&
                callbacks[0].Evidence.LookupFailed && callbacks[0].Evidence.PermanentFailure &&
                callbacks[0].Evidence.DeletedUtc is null && callbacks[0].Evidence.ContentHash is null &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == before + 1 &&
                h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                "inventory delivers terminal Unknown once without caching it as successful history");
        }

        await TransientEvidenceRetryChecksAsync();
    }

    private static async Task TransientEvidenceRetryChecksAsync()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            using var h = new Harness();
            h.Wire.SeedDeletedEntry("held.txt");
            h.Wire.RevisionResponseOverride = new { is_deleted = false, entries = Array.Empty<object>() };
            var clock = new ResilienceChecks.Clock();
            var delays = new List<TimeSpan>();
            h.Client.ReadRetryTime = clock;
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                delays.Add(delay);
                clock.Advance(delay);
                return Task.CompletedTask;
            };
            h.Wire.Script("/2/files/list_revisions", status, 1);
            var callbacks = new List<SyncRemoteDeletionEvidence>();
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "held.txt" };
            var result = await h.Client.LookupDeletionEvidenceForV2Async(["held.txt"], deleted, deleted,
                null, null, CancellationToken.None, (_, evidence) => callbacks.Add(evidence));
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 2 &&
                delays.Count > 0 && delays.All(delay => delay > TimeSpan.Zero) &&
                callbacks.Count == 1 && callbacks[0] == result["held.txt"] && !callbacks[0].LookupFailed,
                status + ": transient revision read still backs off and retries before answering");

            // RQ-052: exhausted provider answers spend the held lane's answered-run budget.
            h.Client.ReadTransientBudget = TimeSpan.FromSeconds(3);
            h.Wire.Script("/2/files/list_revisions", status, 64);
            callbacks.Clear();
            delays.Clear();
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var exhausted = await h.Client.LookupDeletionEvidenceForV2Async(["held.txt"], deleted, deleted,
                null, null, CancellationToken.None, (_, evidence) => callbacks.Add(evidence));
            Require(exhausted["held.txt"].LookupFailed && !exhausted["held.txt"].PermanentFailure &&
                callbacks.Count == 1 && callbacks[0] == exhausted["held.txt"] && callbacks[0].Answered && delays.Count > 0 &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") > before + 1,
                status + ": exhausted answered history reaches the held lane once after bounded transport retries");
        }
    }
}
