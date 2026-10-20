using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>One open needs-decision path observed fresh from both endpoints under the run gate (decision 64).</summary>
internal sealed record DecisionSnapshot(string NoticeId, string Path, ItemState? A, ItemState? B, string Token);

// Decision 64: the needs-decision paths' fresh observation, their version token and the one verb that applies a
// decision. The token pins the exact notice id and both sides' current state, so a case-variant notice is never
// confused and a path that changed since the user looked is refused before any effect.
public sealed partial class SyncRun
{
    private const char DecisionTokenSeparator = ':';
    private const string DecisionNoLongerOpen = "That decision is no longer open; nothing was changed.";
    private const string DecisionPathMismatch = "The decision token does not match that path; run decisions list again.";
    private const string DecisionChangedSinceLooked = "The file changed since you looked; run decisions list again.";
    // Owner choice (Astra round 3): a folder decision's token covers only the folder object, not its descendants, so
    // resolving one is refused until a subtree fingerprint exists (backlog B-172). The user decides the files inside.
    private const string DecisionFolderNotResolvable =
        "folder decisions can't be resolved yet — decide the files inside, or use a per-path choice";

    /// <summary>The reason a resolved decision could not be applied; the CLI surfaces it with the run result.</summary>
    internal const string DecisionNotApplied = "decision could not be applied";

    /// <summary>
    /// Fresh both-sides observations and version tokens for these open decisions, under the run gate. Each side is
    /// observed at the path directly, so a change on disk is seen even though the path is left out of the scan while
    /// its notice is open.
    /// </summary>
    internal Task<IReadOnlyList<DecisionSnapshot>> ObserveDecisionsAsync(string pairId,
        IReadOnlyList<(string NoticeId, string Path)> decisions, CancellationToken ct) =>
        RunExclusiveAsync(null, async (_, token) =>
        {
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
            var snapshots = new List<DecisionSnapshot>(decisions.Count);
            if (decisions.Count > 0) await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
            var a = _endpoints.Writer(pair.A);
            var b = _endpoints.Writer(pair.B);
            foreach (var (noticeId, path) in decisions)
            {
                var observedA = await ObserveAsync(a, path, token).ConfigureAwait(false);
                var observedB = await ObserveAsync(b, path, token).ConfigureAwait(false);
                snapshots.Add(new DecisionSnapshot(noticeId, path, observedA, observedB,
                    Token(noticeId, observedA, observedB)));
            }
            return (IReadOnlyList<DecisionSnapshot>)snapshots;
        }, ct);

    /// <summary>
    /// Applies the chosen side on the decision pinned by <paramref name="decisionToken"/>: the path is released from
    /// the pending set and planned once with <paramref name="winner"/> as the rule-32 winner. The token is re-derived
    /// from a fresh observation in the same gate hold and must match. The notice closes only on evidence (the winner's
    /// effect completed and no relevant effect is unfinished, or both sides now hold equal content); otherwise it stays
    /// open and this reports "decision could not be applied".
    /// </summary>
    public Task<RunReport> ResolveDecisionAsync(string pairId, string relativePath, string decisionToken, Side winner,
        IProgress<RunProgress> progress, CancellationToken ct) =>
        RunExclusiveAsync(progress, async (sink, token) =>
        {
            var noticeId = DecisionNoticeId(decisionToken) ?? throw new SyncRunRefusedException(DecisionNoLongerOpen);
            var notice = _store.OpenNotices(pairId).FirstOrDefault(open =>
                string.Equals(open.NoticeId, noticeId, StringComparison.Ordinal) && open.Kind == NoticeKind.UserDecision)
                ?? throw new SyncRunRefusedException(DecisionNoLongerOpen);
            if (notice.ConflictVersions is not null)
                throw new SyncRunRefusedException("This notice identifies a particular incoming item. Use its rename, replace or dismiss options instead of a generic path winner.");
            if (!PairPath.Same(notice.Path, relativePath))
                throw new SyncRunRefusedException(DecisionPathMismatch);
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
            await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
            var observedA = await ObserveAsync(_endpoints.Writer(pair.A), notice.Path, token).ConfigureAwait(false);
            var observedB = await ObserveAsync(_endpoints.Writer(pair.B), notice.Path, token).ConfigureAwait(false);
            if (observedA?.Kind == ItemKind.Folder || observedB?.Kind == ItemKind.Folder)
                throw new SyncRunRefusedException(DecisionFolderNotResolvable);
            if (!string.Equals(decisionToken, Token(noticeId, observedA, observedB), StringComparison.Ordinal))
                throw new SyncRunRefusedException(DecisionChangedSinceLooked);
            var report = await IncrementalCoreAsync(pairId,
                new Dictionary<string, Side>(StringComparer.OrdinalIgnoreCase) { [notice.Path] = winner },
                notice.NoticeId, sink, token);
            // The notice closes only on evidence, so a still-open one means the decision did not take effect.
            if (!_store.OpenNotices(pairId).Any(open => open.NoticeId == notice.NoticeId)) return report;
            var (refusedA, refusedB) = ProviderRefusedPaths.Load(_store, pair);
            var refused = _store.Live(pair.A.RootId, [notice.Path]).Any(refusedA.Contains) ||
                _store.Live(pair.B.RootId, [notice.Path]).Any(refusedB.Contains);
            var reason = DecisionNotApplied + ": " + (refused ? ProviderNameRefusedException.Detail : NotAppliedReason(pairId, notice.Path));
            if (refused)
                return report with { DecisionFailureReason = reason,
                    Outcome = report.Outcome == RunOutcomes.Complete ? RunOutcomes.Partial : report.Outcome };
            return report with
            {
                Failures = report.Failures.Append(new ActionResult(notice.NoticeId!, Outcome.Failed, null, null,
                    null, reason)).Take(_settings.FailureSampleLimit).ToArray(),
                DecisionFailureReason = reason,
                Failed = report.Failed + 1,
                Outcome = string.Equals(report.Outcome, RunOutcomes.Complete, StringComparison.Ordinal)
                    ? RunOutcomes.Partial
                    : report.Outcome
            };
        }, ct);

    /// <summary>The notice id a decision token pins, or null when the token is malformed.</summary>
    internal static string? DecisionNoticeId(string token)
    {
        var cut = token.IndexOf(DecisionTokenSeparator);
        return cut <= 0 ? null : token[..cut];
    }

    // The token pins the notice id and both sides' freshly observed state; any of it changing makes the token differ.
    private static string Token(string noticeId, ItemState? a, ItemState? b) =>
        noticeId + DecisionTokenSeparator + ShortId.Hex16(Part(a), Part(b));

    private static string Part(ItemState? item) => item is null
        ? "absent"
        : item.ObjectId + "|" + item.Size.ToString(CultureInfo.InvariantCulture) + "|" +
          item.Mtime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + (item.Hash ?? "-");

    // A path that cannot be observed fails closed: the decision is refused, never applied on a guess.
    private static async Task<ItemState?> ObserveAsync(IEndpointWriter writer, string path, CancellationToken ct)
    {
        try
        {
            return await writer.ObserveAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SyncRunRefusedException("This path could not be read for the decision: " + error.Message);
        }
    }

    // Why a decided path could not be closed: the winner's side is gone, one side only, or the sides still differ.
    private string NotAppliedReason(string pairId, string path)
    {
        var pair = _store.Pair(pairId);
        if (pair is null) return "the pair is no longer available";
        var a = _store.Live(pair.A.RootId, [path]).FirstOrDefault();
        var b = _store.Live(pair.B.RootId, [path]).FirstOrDefault();
        if (a is null && b is null) return "the item is no longer present on either side";
        if (a is null || b is null) return "the item is present on only one side; nothing was copied";
        return "the two sides still differ";
    }
}
