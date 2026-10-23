using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

public sealed partial class DropboxPilotClient
{
    // Scheduling hints only, never cached presence/absence evidence. Saturate rather than evict:
    // a large folder must not be rescanned for every bounded request page.
    private readonly HashSet<string> _absenceListedParents = new(StringComparer.Ordinal);
    private const int AbsenceParentHintLimit = 256;

    internal async Task<IReadOnlyList<AbsenceObservation>> ProbeSourceAbsenceAsync(
        IReadOnlyList<(string ObjectId, string HistoricalPath, string Path)> requests,
        SemaphoreSlim reads, CancellationToken ct)
    {
        if (requests.Count > 128) throw new ArgumentOutOfRangeException(nameof(requests));
        if (requests.Count == 0) return Array.Empty<AbsenceObservation>();
        await using var verification = SyncProviderAccountVerificationScope.Begin(ct);
        await reads.WaitAsync(ct).ConfigureAwait(false);
        try { await VerifyRootIdentityAsync(ct).ConfigureAwait(false); }
        finally { reads.Release(); }

        var present = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var wantedIds = requests.Select(x => x.ObjectId)
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        // Normalise per item: a malformed path is inconclusive for that item only, never the whole page.
        var pathFailures = new string?[requests.Count];
        var parents = new List<string>(requests.Count * 2);
        for (var index = 0; index < requests.Count; index++)
        {
            try
            {
                parents.Add(Parent(requests[index].Path));
                parents.Add(Parent(requests[index].HistoricalPath));
            }
            catch (Exception error) when (!IsAccessRefusal(error) && StorageFailureClassifier.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
            { pathFailures[index] = AbsenceObservation.Cause(error); }
        }
        await Parallel.ForEachAsync(parents.Distinct(StringComparer.Ordinal).Where(ClaimParent).ToArray(),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (parent, token) =>
            {
                await reads.WaitAsync(token).ConfigureAwait(false);
                try { await ReadPresentIdsAsync(parent, wantedIds, present, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (!IsAccessRefusal(error) && StorageFailureClassifier.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
                { /* Optional presence shortcut only; exact identity/path probes remain authoritative. */ }
                finally { reads.Release(); }
            }).ConfigureAwait(false);

        var result = new AbsenceObservation[requests.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, requests.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (index, token) =>
            {
                var request = requests[index];
                try
                {
                    // Item-local validation, never batch-wide: a malformed path or empty identity
                    // leaves only this member inconclusive (blocked plus retry) and never absent.
                    if (pathFailures[index] is { } pathFailure) { result[index] = new(null, pathFailure); return; }
                    if (string.IsNullOrEmpty(request.ObjectId))
                    { result[index] = new(null, "the source identity is empty"); return; }
                    if (present.ContainsKey(request.ObjectId)) { result[index] = new(false, null); return; }
                    await reads.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        // A parent listing can prove presence, never global identity absence: the item
                        // might have moved outside both the parent and the selected sync root.
                        if (await ObjectExistsInVerifiedRootAsync(request.ObjectId, token, withinSyncRoot: true).ConfigureAwait(false))
                        { result[index] = new(false, null); return; }
                        // Preserve the existing replacement-at-path safeguard, using fresh observations.
                        var current = await FindEntryInVerifiedRootAsync(request.Path, token).ConfigureAwait(false);
                        if (current is not null) { result[index] = new(false, null); return; }
                        result[index] = new(string.Equals(PilotPath.DropboxComparisonKey(request.Path),
                            PilotPath.DropboxComparisonKey(request.HistoricalPath), StringComparison.Ordinal) ||
                            await FindEntryInVerifiedRootAsync(request.HistoricalPath, token).ConfigureAwait(false) is null, null);
                    }
                    finally { reads.Release(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (!IsAccessRefusal(error) && StorageFailureClassifier.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
                { result[index] = new(null, AbsenceObservation.Cause(error)); }
            }).ConfigureAwait(false);
        // Only root verification fails the page; item failures stay inconclusive for that item.
        return result;

        bool ClaimParent(string parent)
        {
            lock (_absenceListedParents)
                return _absenceListedParents.Count < AbsenceParentHintLimit && _absenceListedParents.Add(parent);
        }
        static string Parent(string path)
        {
            var normalized = PilotPath.DropboxComparisonKey(PilotPath.NormalizeDropboxRelative(path));
            var slash = normalized.LastIndexOf('/');
            return slash < 0 ? "" : normalized[..slash];
        }
    }

    private async Task ReadPresentIdsAsync(string parent, IReadOnlySet<string> wantedIds,
        System.Collections.Concurrent.ConcurrentDictionary<string, byte> present, CancellationToken ct)
    {
        // Only one response page and the bounded requested IDs are retained, even in a huge folder.
        JsonDocument? page = await PostJsonAllowingMissingPathAsync(
            "https://api.dropboxapi.com/2/files/list_folder", new
            {
                path = parent.Length == 0 ? RequiredRootId() : RequiredRootId() + "/" + parent,
                recursive = false, include_deleted = false, include_media_info = false, limit = 1000
            }, ct).ConfigureAwait(false);
        if (page is null) return; // Still requires a global ID lookup; this is not absence evidence.
        try
        {
            string? previousCursor = null;
            for (var pages = 0; ; pages++)
            {
                if (pages >= MaximumContinuationPages)
                    throw new InvalidDataException("Dropbox exceeded the bounded parent-listing page limit.");
                foreach (var entry in page.RootElement.GetProperty("entries").EnumerateArray())
                {
                    var tag = entry.GetProperty(".tag").GetString();
                    if (tag is not ("file" or "folder")) continue;
                    var id = entry.GetProperty("id").GetString();
                    if (id is not null && wantedIds.Contains(id)) present.TryAdd(id, 0);
                }
                if (!page.RootElement.GetProperty("has_more").GetBoolean()) break;
                var cursor = RequireCursor(page.RootElement, "Dropbox parent-listing cursor is absent.");
                if (string.Equals(cursor, previousCursor, StringComparison.Ordinal))
                    throw new InvalidDataException("Dropbox repeated a parent-listing cursor.");
                previousCursor = cursor;
                page.Dispose();
                page = null;
                page = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder/continue",
                    new { cursor }, ct).ConfigureAwait(false);
            }
        }
        finally { page?.Dispose(); }
    }
}
