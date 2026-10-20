using System.IO;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _localRootProblems = new(StringComparer.Ordinal);
    private bool _checkingLocalRoots;
    private DateTimeOffset _lastLocalRootCheck;

    // LH4: one narrow, default-off fixture opt-in for this readonly check. The saved-definition startup load
    // still suppresses the background pass in every isolated fixture; only a focused check that owns its own
    // disposable folders turns it on for its own duration and restores it in its own finally. The interval
    // guard, the in-flight guard and every real production path are unchanged.
    internal static bool LocalRootHealthEnabledForTest { get; set; }

    // LH5: one narrow, default-null observation hook. It never replaces a decision: with no value the real
    // drive-attribute check still decides, and a focused check sets it only to count and to gate the exact
    // paths observed. It adds no provider, credential, persisted setting, service or endpoint.
    internal static Func<string, bool?>? LocalRootObserverForTest { get; set; }

    // LH1/LH3: the bounded captured health plan of one pair. Configuration is the exact ordered endpoint
    // configuration the observation was captured against, so a result that returns after the pair's own
    // configuration changed, or after the pair was removed, can be discarded instead of applied.
    private sealed record LocalRootHealthPlan(string PairId, string Configuration,
        IReadOnlyList<LocalRootHealthTarget> Targets, bool Observable);

    private sealed record LocalRootHealthTarget(string SideLabel, string Path);

    private enum LocalRootHealthOutcome { Clear, Retain, Missing }

    // A root-attribute check is independent of sync/pause. It neither enumerates children, opens file
    // contents, contacts a provider nor claims the saved folder identity still matches. Every structurally
    // defined Local endpoint on both ordered sides is covered: a legacy pair with no endpoint document keeps
    // its one real local-root check, and a valid pair with no local endpoint performs no probe at all.
    private async Task RefreshLocalRootHealthAsync(bool force = false)
    {
        if (_closing || SuppressLoadedStartupForTest && !LocalRootHealthEnabledForTest || _checkingLocalRoots ||
            !force && DateTimeOffset.UtcNow - _lastLocalRootCheck < TimeSpan.FromSeconds(30)) return;
        _checkingLocalRoots = true;
        var plans = _pairSet.Pairs.Select(CaptureLocalRootHealthPlan).ToArray();
        try
        {
            var observations = await Task.Run(() => plans
                .Select(plan => (plan, Observed: plan.Targets
                    .Select(target => (Target: target, Available: ObserveLocalRootForHealth(target.Path))).ToArray()))
                .ToArray());
            if (_closing) return;
            var changed = false;
            foreach (var (plan, observed) in observations)
            {
                // LH3: an observation belongs to the exact pair id and the exact captured local/endpoint
                // configuration. A pair that was removed, or whose declared configuration changed, before the
                // observation returned is ignored rather than applied to another configuration.
                var current = _pairSet.Pairs.FirstOrDefault(pair =>
                    string.Equals(pair.Id, plan.PairId, StringComparison.Ordinal));
                if (current is null || !string.Equals(CaptureLocalRootHealthPlan(current).Configuration,
                        plan.Configuration, StringComparison.Ordinal)) continue;
                var (outcome, message) = AggregateLocalRootHealth(plan, observed);
                switch (outcome)
                {
                    case LocalRootHealthOutcome.Clear:
                        changed |= _localRootProblems.Remove(plan.PairId);
                        break;
                    case LocalRootHealthOutcome.Missing:
                        changed |= _localRootProblems.GetValueOrDefault(plan.PairId) != message;
                        _localRootProblems[plan.PairId] = message;
                        break;
                    // Retain: an inaccessible, offline, unsupported or unknown local folder is not proof of
                    // absence and never clears unresolved prior evidence.
                }
                UpdatePairAttentionPresentation(plan.PairId);
            }
            if (changed) { _attentionFingerprint = ""; RefreshWorkspaceFeeds(); RefreshWorkspace(); RefreshExplorerRootStates(); }
        }
        finally { _lastLocalRootCheck = DateTimeOffset.UtcNow; _checkingLocalRoots = false; }
    }

    // LH1/LH2: the pair's own typed ordered endpoints are described by the existing shared capture, so a
    // legacy pair with no document keeps its saved local root, a reversed cloud/local pair checks its real
    // right local endpoint, a local/local pair checks both, and a valid cloud/cloud pair has none. A cloud
    // identifier, an empty compatibility root and a virtual compatibility port are never read as a local path.
    private static LocalRootHealthPlan CaptureLocalRootHealthPlan(SyncPair pair)
    {
        try
        {
            var endpoints = SyncEndpointContext.Capture(pair);
            var targets = new[] { endpoints.Left, endpoints.Right }
                .Where(side => side.IsLocal)
                .Select(side => new LocalRootHealthTarget(
                    side.Side == SyncPairSide.Left ? "left" : "right", side.LocalPath!))
                .ToArray();
            return new LocalRootHealthPlan(pair.Id,
                DescribeConfiguration(endpoints.Left) + "|" + DescribeConfiguration(endpoints.Right),
                targets, true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // LH2: a declared endpoint description that cannot be read safely stays unknown. It is never
            // reinterpreted through the legacy local-root field and never clears unresolved prior evidence.
            return new LocalRootHealthPlan(pair.Id, "unreadable", [], false);
        }
    }

    private static (LocalRootHealthOutcome Outcome, string Message) AggregateLocalRootHealth(
        LocalRootHealthPlan plan, IReadOnlyList<(LocalRootHealthTarget Target, bool? Available)> observed)
    {
        if (!plan.Observable) return (LocalRootHealthOutcome.Retain, string.Empty);
        // LH2: a definitely absent local folder on either side is reported with its own ordered side name,
        // and a present other side never clears it.
        var absent = observed.Where(item => item.Available == false).ToArray();
        if (absent.Length > 0)
            return (LocalRootHealthOutcome.Missing, string.Join(" ", absent.Select(item =>
                $"Local folder unavailable on the {item.Target.SideLabel}: {item.Target.Path}.")) +
                " Choose another folder to repair this pair.");
        // LH2: clear only when every recorded local folder was observed present. A configuration that records
        // no local endpoint at all cannot carry a local-root problem, so a stale one is cleared for it.
        if (plan.Targets.Count == 0) return (LocalRootHealthOutcome.Clear, string.Empty);
        return observed.Any(item => item.Available is null)
            ? (LocalRootHealthOutcome.Retain, string.Empty)
            : (LocalRootHealthOutcome.Clear, string.Empty);
    }

    private static string DescribeConfiguration(SyncEndpointContextSide side) => side.IsLocal
        ? "local:" + (side.LocalPath ?? string.Empty)
        : side.Kind + ":" + (side.ConnectionId ?? string.Empty) + ":" + (side.FolderId ?? string.Empty) +
          ":" + (side.FolderPath ?? string.Empty);

    private static bool? ObserveLocalRootForHealth(string path) =>
        LocalRootObserverForTest is not null ? LocalRootObserverForTest(path) : ObserveLocalRoot(path);

    internal static bool? ObserveLocalRoot(string path)
        => SyncLocalRootAvailability.Observe(path);
}
