using System.IO;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// Consumer proof: capture the callback the actual window constructs, then supply
// scope INPUT. Actual RunScheduledAsync/RunPairsAsync admission chooses targets;
// only the downstream transfer boundary is observed, never replaced admission.
internal static class GoogleMonitorScopeChecks
{
    internal static async Task RunAsync(MainWindow window, SyncPair template, SyncPairStore store)
    {
        var priorPairs = store.Load();
        // Initialize the same authority Host before adding temporary legacy input.
        // Otherwise first initialization imports A/B durably and restoring only the
        // legacy JSON leaves eligible authority pairs in later all-pairs UI checks.
        var priorHostPairs = await window.AuthorityHost.LoadPairsAsync();
        Require(SameIds(priorHostPairs, priorPairs),
            "original Host and legacy pair identities agree before temporary scope input");
        var priorDispatch = MainWindow.PairRunDispatcherForTest;
        var priorConfigure = window.ScopedMonitorConfiguratorForTest;
        var automation = window.AuthorityHost.LoadAutomation();
        if (!automation.Enabled) throw new InvalidDataException("M10 consumer fixture requires isolated automation enabled.");
        if (template.EndpointSchemaVersion is not null)
            throw new InvalidDataException("Use the existing isolated focused-UI legacy template for M10 scope input.");
        var localA = Path.Combine(template.LocalRoot, "m10-scope-" + Guid.NewGuid().ToString("N"));
        var localB = Path.Combine(template.LocalRoot, "m10-scope-" + Guid.NewGuid().ToString("N"));
        var dispatches = new List<string[]>();
        Func<IReadOnlySet<string>, Task<bool>>? callback = null;
        try
        {
            var a = CreatePair(localA, "A");
            var b = CreatePair(localB, "B");
            await store.SaveAsync(new SyncPairSet(priorPairs.Pairs.Concat(new[] { a, b }).ToArray(), a.Id));
            window.ScopedMonitorConfiguratorForTest = (pairs, request, _) =>
            {
                if (!pairs.Any(pair => pair.Id == a.Id) || !pairs.Any(pair => pair.Id == b.Id))
                    throw new InvalidDataException("Both controlled eligible pair inputs were not configured.");
                callback = request;
                return Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>(pairs.Select(pair =>
                    new SyncAuthorityMonitorStartResult(pair.Id, "Monitoring", null, null)).ToArray());
            };
            MainWindow.PairRunDispatcherForTest = (pairs, trigger) =>
            {
                if (trigger != "Monitor") throw new InvalidDataException("Wrong actual monitor trigger.");
                dispatches.Add(pairs.Select(pair => pair.Id).Order(StringComparer.Ordinal).ToArray());
                return Task.CompletedTask;
            };
            await window.ConfigureLocalMonitorsForTestAsync(automation);
            var current = callback ?? throw new InvalidDataException("Actual scoped UI callback was not constructed.");
            await current(Scope(a.Id));
            Require(dispatches.Count == 1 && dispatches[0].SequenceEqual(new[] { a.Id }),
                "actual UI scoped admission runs A, not eligible unrelated B");
            await current(Scope("outside-unregistered-pair"));
            Require(dispatches.Count == 1, "outside/unregistered scope cannot dispatch an unrelated pair");
            await current(Scope(a.Id, b.Id));
            Require(dispatches.Count == 2 && dispatches[1].SequenceEqual(new[] { a.Id, b.Id }.Order()),
                "actual UI legitimate plural scope admits exactly both named pairs");

            await window.ConfigureLocalMonitorsForTestAsync(automation);
            var refreshed = callback ?? throw new InvalidDataException("Reconfigured callback absent.");
            var beforeStale = dispatches.Count;
            Require(await current(Scope(a.Id)) && dispatches.Count == beforeStale,
                "obsolete monitor configuration generation cannot dispatch");
            await refreshed(Scope(b.Id));
            Require(dispatches.Count == beforeStale + 1 && dispatches[^1].SequenceEqual(new[] { b.Id }),
                "current generation retains legitimate B admission");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow.PairRunDispatcherForTest = async (pairs, _) =>
            {
                dispatches.Add(pairs.Select(pair => pair.Id).Order(StringComparer.Ordinal).ToArray());
                entered.TrySetResult();
                await release.Task;
            };
            var first = refreshed(Scope(a.Id));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var beforeBusy = dispatches.Count;
                Require(!await refreshed(Scope(b.Id)) && dispatches.Count == beforeBusy,
                    "busy actual UI refuses rather than acknowledging or dispatching a newer B request");
            }
            finally { release.TrySetResult(); await first; }
            await refreshed(Scope(b.Id));
            Require(dispatches[^1].SequenceEqual(new[] { b.Id }), "retained newer scope succeeds after the busy run clears");
        }
        finally
        {
            try
            {
                await store.SaveAsync(priorPairs);
                var restored = await window.AuthorityHost.LoadPairsAsync();
                Require(SameIds(restored, priorHostPairs),
                    "temporary scope input left no retained authority pair after cleanup");
                if (priorHostPairs.SelectedPairId is { } selected)
                    restored = await window.AuthorityHost.SelectPairAsync(selected);
                window.LoadPairSetForTest(restored);
            }
            finally
            {
                MainWindow.PairRunDispatcherForTest = priorDispatch;
                window.ScopedMonitorConfiguratorForTest = priorConfigure;
                // Only the two empty directories this isolated fixture created.
                if (Directory.Exists(localA)) Directory.Delete(localA, recursive: false);
                if (Directory.Exists(localB)) Directory.Delete(localB, recursive: false);
            }
        }

        SyncPair CreatePair(string local, string label)
        {
            if (Directory.Exists(local) || File.Exists(local)) throw new IOException("M10 fixture directory is not fresh.");
            Directory.CreateDirectory(local);
            using var lease = PilotDirectoryLease.Open(local);
            return template with { Id = Guid.NewGuid().ToString("N")[..20], Name = "M10 scope " + label,
                LocalRoot = local, LocalVolumeSerialNumber = lease.Identity.VolumeSerialNumber,
                LocalRootFileId = lease.Identity.FileId, DropboxRoot = "/M10 scope " + label,
                DropboxFolderId = "id:m10-scope-" + label, ControlNamespace = null, RecoveryRoot = null,
                Enabled = true, InitialReviewApproved = true, PolicyRevision = 1 };
        }
    }

    private static IReadOnlySet<string> Scope(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);
    private static bool SameIds(SyncPairSet left, SyncPairSet right) =>
        left.Pairs.Select(pair => pair.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(right.Pairs.Select(pair => pair.Id).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static void Require(bool passed, string description)
    {
        if (!passed) throw new InvalidDataException("M10 " + description);
        Console.WriteLine("PASS M10 " + description);
    }
}
