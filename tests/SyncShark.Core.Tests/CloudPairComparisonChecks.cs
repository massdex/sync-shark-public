using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// CC2A focused proofs: one shared algorithm-aware asynchronous comparison wired into the real
// SyncAuthorityInitialPlanner and SyncAuthorityReconciliationPlanner over a real SQLite authority database
// and real observations produced by SyncAuthorityObservationCoordinator.
//
// The fixture contains no provider client, no credential, no HttpClient and no host: the cloud side is a
// scripted ISyncRemoteStore that returns exactly the entries a case needs and the common-digest resolver is
// a scripted input. Neither is claimed as provider proof. Every planner outcome below is produced by the
// real production planner reading real retained observations from SQLite.
//
// Provider download, root registration, effects, Host wiring and UI are out of scope for this packet and
// are not claimed here.
internal static class CloudPairComparisonChecks
{
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunInitialProgressAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh isolated progress fixture.");
        var h = await Harness.CreateAsync(root, "initial-progress");
        for (var i = 0; i < 260; i++)
        {
            var path = $"item-{i:D3}.txt";
            await h.WriteLocalAsync(path, "same");
            h.ScriptFile(path, "same", DropboxIdentity("same"));
        }
        await h.WriteLocalAsync("conflict.txt", "left");
        h.ScriptFile("conflict.txt", "right", DropboxIdentity("right"));
        await h.WriteLocalAsync("left-only.txt", "left only");
        h.ScriptFile("right-only.txt", "right only", DropboxIdentity("right only"));
        h.Pair = h.Pair with { SelectionRules = [new SyncSelectionRule("excluded", false)] };
        await h.WriteLocalAsync("excluded/keep.txt", "excluded content");
        h.ScriptDirectory("excluded");
        h.ScriptFile("excluded/keep.txt", "excluded content", DropboxIdentity("excluded content"));
        await h.ObserveAsync();
        var progress = new List<SyncComparisonProgress>();
        var planner = new SyncAuthorityInitialPlanner(h.Db);
        var decisions = await planner.AdoptAsync(h.Pair.Id, onProgress: async item =>
        {
            lock (progress) progress.Add(item);
            await Task.Yield();
        });
        var rows = progress.Where(item => item.RelativePath != h.Pair.Id).ToArray();
        h.Assert(rows.Length == 263 && rows.Select(item => item.RelativePath).Distinct().Count() == 263,
            "every exact path is logged across the 256-event batch boundary and final partial batch");
        h.Assert(rows.Select(item => item.CompletedChecks).SequenceEqual(Enumerable.Range(1, 263)) &&
            rows.All(item => item.TotalChecks == 263 && item.ProgressUnit == "paths processed"),
            "progress has ordered actual completed counts and a known total");
        h.Assert(progress[0].CompletedChecks == 0 && progress[^1].CompletedChecks == 263 &&
            progress[^1].Detail.StartsWith("Finished recording", StringComparison.Ordinal),
            "start precedes inventory loading and final completion follows all delivered rows");
        h.Assert(rows.Single(item => item.RelativePath == "conflict.txt").Detail.Contains("neither side was changed") &&
            rows.Single(item => item.RelativePath == "item-000.txt").Detail.Contains("synchronization baseline") &&
            rows.Single(item => item.RelativePath == "left-only.txt").Detail.Contains("no file was transferred"),
            "the log distinguishes matching baselines, retained conflicts and one-sided review items");
        h.Assert(decisions.Count(item => item.Kind == SyncDecisionKind.Conflict) == 1 &&
            await h.SyncPointsAsync() == 260 && await h.RunnableOperationsAsync() == 0,
            "reporting preserves baseline/conflict semantics and creates no file effects");
        using var cancelled = new CancellationTokenSource();
        progress.Clear();
        var observedCancellation = false;
        try
        {
            await planner.AdoptAsync(h.Pair.Id, cancelled.Token, item =>
            {
                progress.Add(item);
                if (item.CompletedChecks == 257) cancelled.Cancel();
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { observedCancellation = true; }
        h.Assert(observedCancellation && progress.Count(item => item.RelativePath != h.Pair.Id) == 257 &&
            !progress.Any(item => item.Detail.StartsWith("Finished recording", StringComparison.Ordinal)),
            "cancellation drains the partial batch without claiming unfinished paths or overall success");
        h.Assert(await h.SyncPointsAsync() == 260 && await h.RunnableOperationsAsync() == 0,
            "cancelled repeat does not duplicate baselines or create effect authority");
        progress.Clear();
        var retained = await planner.RetainReviewConflictsAsync(h.Pair.Id,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "conflict.txt" }, CancellationToken.None,
            item => { progress.Add(item); return Task.CompletedTask; });
        h.Assert(retained.Count == 1 && progress.Single(item => item.RelativePath == "conflict.txt") is
            { Phase: "Retaining First Sync Conflicts", CompletedChecks: 1, TotalChecks: 1 },
            "later conflict-only review uses the same reporting path and exact selected total");
        var review = await h.Db.LoadBootstrapEvidenceAsync(h.Pair.Id);
        h.Assert(review is { TotalItems: 263, LocalOnlyItems: 1, DropboxOnlyItems: 1, ConflictItems: 1 } &&
            review.Items.All(item => !item.RelativePath.StartsWith("excluded", StringComparison.Ordinal)),
            "narrow SQL projection preserves review totals and excludes only already-excluded paths");
        var filtered = await h.Db.LoadBootstrapEvidencePageAsync(h.Pair.Id, limit: 20,
            search: "item-", group: "Identical", itemKind: "Files", fileType: "txt");
        h.Assert(filtered is { TotalItems: 263, TotalMatchingItems: 260, HasMore: true } &&
            filtered.Items.Count == 20 && filtered.Items[0].RelativePath == "item-000.txt" &&
            filtered.LocalScanGenerationId == review!.LocalScanGenerationId &&
            filtered.DropboxScanGenerationId == review.DropboxScanGenerationId,
            "scalar totals keep global counts separate from filtered count and preserve exact scan authority");
        var lastPage = await h.Db.LoadBootstrapEvidencePageAsync(h.Pair.Id, limit: 20,
            offset: 250, search: "item-", group: "Identical", itemKind: "Files", fileType: "txt");
        h.Assert(lastPage is { TotalItems: 263, TotalMatchingItems: 260, HasMore: false } &&
            lastPage.Items.Count == 10 && lastPage.Items[^1].RelativePath == "item-259.txt",
            "last filtered page keeps full counts and stable order without window totals");
        var pastEnd = await h.Db.LoadBootstrapEvidencePageAsync(h.Pair.Id, limit: 20,
            offset: 500, search: "item-", group: "Identical");
        h.Assert(pastEnd is { TotalItems: 263, TotalMatchingItems: 260, HasMore: false } &&
            pastEnd.Items.Count == 0,
            "empty page beyond the end still reports complete filtered and global totals");
        var oneSided = await h.Db.LoadBootstrapEvidencePageAsync(h.Pair.Id, group: "Local only");
        h.Assert(oneSided is { TotalItems: 263, TotalMatchingItems: 1, LocalOnlyItems: 1, DropboxOnlyItems: 1, ConflictItems: 1 } &&
            oneSided.Items.Single().RelativePath == "left-only.txt",
            "group filtering never changes global category totals");
        var noType = await h.Db.LoadBootstrapEvidencePageAsync(h.Pair.Id, fileType: "pdf");
        h.Assert(noType is { TotalItems: 263, TotalMatchingItems: 0, HasMore: false } && noType.Items.Count == 0,
            "no-match type filter preserves complete global totals through summary query");
        foreach (var phase in new[] { "rows", "summary", "moves" })
        {
            using var readCancellation = new CancellationTokenSource();
            var nativeTicks = 0;
            h.Db.BootstrapReadProgressForTest = current =>
            {
                if (current != phase) return;
                nativeTicks++;
                readCancellation.Cancel();
            };
            var interrupted = false;
            try
            {
                await h.Db.LoadBootstrapEvidenceAsync(h.Pair.Id,
                    search: phase == "summary" ? "nonexistent-filter-for-cancellation" : null,
                    cancellationToken: readCancellation.Token);
            }
            catch (OperationCanceledException) { interrupted = true; }
            finally { h.Db.BootstrapReadProgressForTest = null; }
            h.Assert(interrupted && nativeTicks > 0,
                "the actual " + phase + " comparison VM is interrupted, not merely a token check before SQL");
            h.Assert((await h.Db.LoadBootstrapEvidenceAsync(h.Pair.Id))?.TotalItems == 263 &&
                await h.SyncPointsAsync() == 260 && await h.RunnableOperationsAsync() == 0,
                "cancelled " + phase + " read preserves complete inventory/evidence and permits successful reuse");
        }
        Console.WriteLine($"PASS initial planner progress: {h.Assertions} assertions; 263 exact paths, batched drain, cancellation, conflict retention, selected SQL projection and native rows/summary/move cancellation.");
    }

    // Reuse the real observer/initial-planner fixture for retained decision-context proofs. The scripted
    // remote supplies input only; the observer, baseline adoption and reconciliation remain production code.
    internal static async Task<(SyncAuthorityDatabase Database, string PairId)> CreateDecisionBaselineAsync(
        string root, string id, string? pairId = null)
    {
        var fixture = await Harness.CreateAsync(root, id, pairId);
        await fixture.WriteLocalAsync("retained.txt", "retained decision baseline");
        fixture.ScriptFile("retained.txt", "retained decision baseline",
            DropboxIdentity("retained decision baseline"));
        await fixture.ObserveAsync();
        var adopted = await fixture.AdoptInitialAsync();
        if (adopted.Single().Kind != SyncDecisionKind.Baseline || await fixture.SyncPointsAsync() != 1)
            throw new InvalidDataException("The decision-context fixture did not adopt its real baseline.");
        return (fixture.Db, fixture.Pair.Id);
    }

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud-pair comparison fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("A-initial-shared-comparison",
            "InitialPlanner adopts same-algorithm native identity and a verified common digest",
            InitialSharedComparisonAsync);
        await CaseAsync("B-initial-unknown-blocks",
            "InitialPlanner blocks unknown instead of adopting a baseline or claiming a conflict",
            InitialUnknownBlocksAsync);
        await CaseAsync("C-legacy-default-compatibility",
            "An omitted comparer keeps the legacy single-algorithm compatibility policy",
            LegacyDefaultCompatibilityAsync);
        await CaseAsync("D-steady-converged-and-unchanged",
            "Steady convergence adopts and an unchanged baseline needs no comparison",
            SteadyConvergedAsync);
        await CaseAsync("E-steady-one-sided-no-comparison",
            "One-sided new items plan without any cross-side comparison",
            SteadyOneSidedAsync);
        await CaseAsync("F-steady-different-conflicts",
            "Different same-algorithm content keeps the existing conflict behaviour",
            SteadyDifferentAsync);
        await CaseAsync("G-steady-size-mismatch-no-stream",
            "A size mismatch conflicts without any digest stream",
            SteadySizeMismatchAsync);
        await CaseAsync("H-steady-unknown-blocks-no-effect",
            "Incomparable algorithms without a verified digest block and plan no effect",
            SteadyUnknownBlocksAsync);
        await CaseAsync("I-steady-verified-digest-conflicts",
            "A verified common digest that differs produces the existing conflict",
            SteadyVerifiedDigestConflictsAsync);
        await CaseAsync("J-unverified-digest-blocks",
            "Wrong identity, wrong size, invalid digest and missing proof all block",
            UnverifiedDigestBlocksAsync);
        await CaseAsync("K-cache-reuse-and-revision",
            "The bounded exact-key cache reuses and a changed revision invalidates",
            CacheReuseAsync);
        await CaseAsync("L-kind-directory-algorithm-no-stream",
            "Directory, kind mismatch and unregistered algorithms never stream",
            NoStreamAsync);
        await CaseAsync("M-cancellation-propagates",
            "Cancellation through the resolver propagates and plans nothing",
            CancellationAsync);
        await CaseAsync("N-new-path-unknown-blocks",
            "A new both-present path with unknown equality blocks",
            NewPathUnknownAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudPairComparisonChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "cloud-pair-comparison-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud-pair comparison: " + _assertions + " assertions; real SQLite initial and" +
            " steady planner adoption, verified and unverified common digests, bounded cache reuse and" +
            " revision invalidation, unknown blocking with no effect, and cancellation propagation.");
    }

    private static async Task CaseAsync(string id, string label, Func<Harness, Task> body)
    {
        var harness = await Harness.CreateAsync(_root, id);
        try
        {
            await body(harness);
            _assertions += harness.Assertions;
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id,
                ["label"] = label,
                ["result"] = "PASS",
                ["assertions"] = harness.Assertions
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + harness.Assertions + " assertions)");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id,
                ["label"] = label,
                ["result"] = "FAIL",
                ["assertions"] = harness.Assertions,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    // ----- case bodies ---------------------------------------------------------------------------

    private static async Task InitialSharedComparisonAsync(Harness h)
    {
        await h.WriteLocalAsync("same.txt", "same algorithm corpus");
        h.ScriptFile("same.txt", "same algorithm corpus", DropboxIdentity("same algorithm corpus"));
        await h.ObserveAsync();
        var decisions = await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));
        h.Assert(decisions.Single(item => item.RelativePath == "same.txt").Kind == SyncDecisionKind.Baseline,
            "the same-algorithm native identity adopts a first-run baseline; observed " + Describe(decisions));
        h.Assert(await h.SyncPointsAsync() == 1, "the same-algorithm path adopted one synchronization point");
        h.Assert(await h.RunnableOperationsAsync() == 0, "initial adoption plans no effect");

        // The declared algorithms belong to the comparer instance one planner call receives, so the
        // incomparable pair is proved on its own isolated fixture rather than mixed into this run.
        var cross = await Harness.CreateAsync(_root, "A2-initial-verified-common-digest");
        await cross.WriteLocalAsync("cross.txt", "cross provider corpus");
        cross.ScriptFile("cross.txt", "cross provider corpus", Sha256Identity("cross provider corpus"));
        await cross.ObserveAsync();
        var resolver = new ScriptedResolver();
        var requests = new List<SyncAuthorityCommonDigestRequest>();
        resolver.Responder = request =>
        {
            requests.Add(request);
            return Verified(request, Sha256Identity("cross provider corpus"));
        };
        var crossDecisions = await cross.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive,
            resolver));

        h.Assert(crossDecisions.Single(item => item.RelativePath == "cross.txt").Kind ==
            SyncDecisionKind.Baseline,
            "a verified common digest adopts a first-run baseline; observed " + Describe(crossDecisions));
        h.Assert(await cross.SyncPointsAsync() == 1, "the incomparable path adopted one synchronization point");
        h.Assert(await cross.RunnableOperationsAsync() == 0, "the incomparable path plans no effect");
        h.Assert(resolver.Calls == 1, "only the incomparable pair asked for a common digest");
        h.Assert(requests.Count == 1 && requests[0].RelativePath == "cross.txt",
            "the resolver was asked for the exact retained path that needs a common digest");
        h.Assert(requests[0].NativeAlgorithm == SyncContentIdentity.DropboxBlockSha256 &&
            requests[0].RootBindingId == "cc2a-local" && requests[0].ObjectVersionId.Length > 0,
            "the resolver request binds the exact root binding, revision and native algorithm");
        h.Assert(requests[0].NativeHash == DropboxIdentity("cross provider corpus"),
            "the resolver request carries the exact retained native hash it must bind to");
    }

    private static async Task InitialUnknownBlocksAsync(Harness h)
    {
        await h.WriteLocalAsync("twins.txt", "identical hex corpus");
        h.ScriptFile("twins.txt", "identical hex corpus", DropboxIdentity("identical hex corpus"));
        await h.WriteLocalAsync("nohash.txt", "no retained identity");
        h.ScriptFile("nohash.txt", "no retained identity", null);
        await h.ObserveAsync();

        var local = await h.ObservedAsync("local");
        var remote = await h.ObservedAsync("dropbox");
        h.Assert(string.Equals(local.Single(item => item.RelativePath == "twins.txt").ContentHash,
                remote.Single(item => item.RelativePath == "twins.txt").ContentHash,
                StringComparison.OrdinalIgnoreCase),
            "the incomparable-algorithm fixture retains byte-identical hex on both sides");

        var resolver = new ScriptedResolver();
        var decisions = await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive,
            resolver));
        h.Assert(decisions.All(item => item.Kind == SyncDecisionKind.Blocked),
            "identical hex across algorithms and a missing identity both block rather than adopt or conflict; observed " +
            Describe(decisions));
        h.Assert(await h.SyncPointsAsync() == 0, "no baseline was adopted for an unknown comparison");
        h.Assert(await h.OpenConflictsAsync() == 0, "no conflict was invented for an unknown comparison");
        h.Assert(await h.RunnableOperationsAsync() == 0, "an unknown comparison plans no effect");
        h.Assert(resolver.Calls == 1,
            "the unverifiable left side asked once for a digest, and the missing identity asked none");
    }

    private static async Task LegacyDefaultCompatibilityAsync(Harness h)
    {
        await h.WriteLocalAsync("twins.txt", "identical hex corpus");
        h.ScriptFile("twins.txt", "identical hex corpus", DropboxIdentity("identical hex corpus"));
        await h.WriteLocalAsync("nohash.txt", "no retained identity");
        h.ScriptFile("nohash.txt", "no retained identity", null);
        await h.ObserveAsync();

        var decisions = await h.AdoptInitialAsync();
        h.Assert(decisions.Single(item => item.RelativePath == "twins.txt").Kind == SyncDecisionKind.Baseline,
            "the omitted comparer keeps the legacy single-algorithm raw-string equality for existing callers");
        h.Assert(decisions.Single(item => item.RelativePath == "nohash.txt").Kind == SyncDecisionKind.Blocked,
            "a missing identity is Unknown even under the compatibility policy");
        h.Assert(await h.OpenConflictsAsync() == 0, "the compatibility policy records no conflict for a missing identity");

        var local = (await h.ObservedAsync("local")).Single(item => item.RelativePath == "nohash.txt");
        var remote = (await h.ObservedAsync("dropbox")).Single(item => item.RelativePath == "nohash.txt");
        h.Assert((await SyncAuthorityLegacyContentComparison.Instance.CompareAsync(local, remote)).IsUnknown,
            "the named compatibility policy reports a missing identity as Unknown");
    }

    private static async Task SteadyConvergedAsync(Harness h)
    {
        await h.WriteLocalAsync("shared.txt", "first revision");
        h.ScriptFile("shared.txt", "first revision", DropboxIdentity("first revision"));
        await h.ObserveAsync();
        var adoptions = await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));
        h.Assert(adoptions.Single().Kind == SyncDecisionKind.Baseline, "the first run adopts one baseline");

        await h.WriteLocalAsync("shared.txt", "second revision");
        h.ScriptFile("shared.txt", "second revision", DropboxIdentity("second revision"));
        await h.ObserveAsync();
        var resolver = new ScriptedResolver();
        var outcomes = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox, resolver));
        h.Assert(outcomes.Single().Kind == SyncAuthorityPlannerOutcomeKind.Adopted,
            "both sides independently converged to equivalent content and the new point was adopted");
        h.Assert(resolver.Calls == 0, "an identical native algorithm needs no digest resolution");
        h.Assert(await h.RunnableOperationsAsync() == 0, "converged content plans no effect");

        await h.ObserveAsync();
        var unchanged = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox, resolver));
        h.Assert(unchanged.Single().Kind == SyncAuthorityPlannerOutcomeKind.Unchanged,
            "the unchanged baseline matches each side against its own retained version and path");
        h.Assert(resolver.Calls == 0, "an unchanged baseline never invokes the digest resolver");
    }

    private static async Task SteadyOneSidedAsync(Harness h)
    {
        await h.WriteLocalAsync("keep.txt", "shared baseline");
        h.ScriptFile("keep.txt", "shared baseline", DropboxIdentity("shared baseline"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("local-only.txt", "one-sided local");
        h.ScriptFile("remote-only.txt", "one-sided remote", DropboxIdentity("one-sided remote"));
        await h.ObserveAsync();
        var resolver = new ScriptedResolver();
        var outcomes = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox, resolver));

        h.Assert(outcomes.Single(item => item.RelativePath == "keep.txt").Kind ==
            SyncAuthorityPlannerOutcomeKind.Unchanged, "the one-sided run leaves the unchanged baseline alone");
        h.Assert(outcomes.Single(item => item.RelativePath == "local-only.txt").Kind ==
            SyncAuthorityPlannerOutcomeKind.Operation, "a locally only path plans one create operation");
        h.Assert(outcomes.Single(item => item.RelativePath == "remote-only.txt").Kind ==
            SyncAuthorityPlannerOutcomeKind.Operation, "a remote only path plans one create operation");
        h.Assert(resolver.Calls == 0, "a one-sided new item never invokes the digest resolver");
        h.Assert(await h.RunnableOperationsAsync() == 2, "each one-sided item retained exactly one operation");
    }

    private static async Task SteadyDifferentAsync(Harness h)
    {
        await h.WriteLocalAsync("doc.txt", "baseline revision");
        h.ScriptFile("doc.txt", "baseline revision", DropboxIdentity("baseline revision"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("doc.txt", "local revision two");
        h.ScriptFile("doc.txt", "remote revision two", DropboxIdentity("remote revision two"));
        await h.ObserveAsync();
        var resolver = new ScriptedResolver();
        var outcomes = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox, resolver));

        h.Assert(outcomes.Single().Kind == SyncAuthorityPlannerOutcomeKind.Conflict,
            "different same-algorithm content keeps the existing conflict behaviour");
        h.Assert(outcomes.Single().ConflictId is not null, "the conflict retains its exact versions");
        h.Assert(resolver.Calls == 0, "a same-algorithm difference needs no digest resolution");
        h.Assert(await h.OpenConflictsAsync() == 1, "one open conflict was recorded");
        h.Assert(await h.RunnableOperationsAsync() == 0, "a conflict plans no effect");
    }

    private static async Task SteadySizeMismatchAsync(Harness h)
    {
        await h.WriteLocalAsync("size.txt", "baseline revision");
        h.ScriptFile("size.txt", "baseline revision", DropboxIdentity("baseline revision"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("size.txt", "short");
        h.ScriptFile("size.txt", "a much longer remote revision", DropboxIdentity("a much longer remote revision"));
        await h.ObserveAsync();
        var resolver = new ScriptedResolver();
        var outcomes = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox, resolver));

        h.Assert(outcomes.Single().Kind == SyncAuthorityPlannerOutcomeKind.Conflict,
            "a retained size mismatch is a definite difference");
        h.Assert(resolver.Calls == 0, "a size mismatch never streams or resolves a digest");
    }

    private static async Task SteadyUnknownBlocksAsync(Harness h)
    {
        await h.WriteLocalAsync("cross.txt", "first cross revision");
        h.ScriptFile("cross.txt", "first cross revision", DropboxIdentity("first cross revision"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("cross.txt", "second cross revision");
        h.ScriptFile("cross.txt", "second cross revision", DropboxIdentity("second cross revision"));
        await h.ObserveAsync();
        var resolver = new ScriptedResolver();
        var outcomes = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive,
            resolver));

        h.Assert(outcomes.Single().Kind == SyncAuthorityPlannerOutcomeKind.Blocked,
            "incomparable algorithms without a verified digest block instead of adopting or conflicting");
        h.Assert(resolver.Calls == 1, "the unverifiable left side asked for exactly one common digest");
        h.Assert(await h.OpenConflictsAsync() == 0, "an unknown comparison is not a definite conflict");
        h.Assert(await h.RunnableOperationsAsync() == 0, "an unknown comparison plans no effect");
    }

    private static async Task SteadyVerifiedDigestConflictsAsync(Harness h)
    {
        await h.WriteLocalAsync("cross.txt", "first cross revision");
        h.ScriptFile("cross.txt", "first cross revision", DropboxIdentity("first cross revision"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("cross.txt", "second cross revision");
        h.ScriptFile("cross.txt", "second cross revision", Sha256Identity("second cross revision"));
        await h.ObserveAsync();
        var resolver = new ScriptedResolver
        {
            Responder = request => Verified(request, Sha256Identity("some other corpus"))
        };
        var outcomes = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive,
            resolver));

        h.Assert(outcomes.Single().Kind == SyncAuthorityPlannerOutcomeKind.Conflict,
            "two verified common digests that differ are a definite conflict");
        h.Assert(resolver.Calls == 1, "the incomparable left side was resolved exactly once");
        h.Assert(await h.OpenConflictsAsync() == 1, "the verified difference retained one open conflict");
        h.Assert(await h.RunnableOperationsAsync() == 0, "the conflict plans no effect");
    }

    private static async Task UnverifiedDigestBlocksAsync(Harness h)
    {
        await h.WriteLocalAsync("cross.txt", "first cross revision");
        h.ScriptFile("cross.txt", "first cross revision", DropboxIdentity("first cross revision"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("cross.txt", "second cross revision");
        h.ScriptFile("cross.txt", "second cross revision", Sha256Identity("second cross revision"));
        await h.ObserveAsync();

        var badVersion = new ScriptedResolver
        {
            Responder = request => new SyncAuthorityCommonDigest(request.RootBindingId,
                request.ObjectVersionId + "-other", request.Size, request.NativeAlgorithm, request.NativeHash,
                Sha256Identity("second cross revision"), request.Size)
        };
        await h.AssertBlockedWithoutEffectAsync("a digest bound to another object version", badVersion);
        h.Assert(badVersion.Calls == 1, "the wrongly bound digest was actually requested");

        var badSize = new ScriptedResolver
        {
            Responder = request => new SyncAuthorityCommonDigest(request.RootBindingId, request.ObjectVersionId,
                request.Size + 1, request.NativeAlgorithm, request.NativeHash,
                Sha256Identity("second cross revision"), request.Size + 1)
        };
        await h.AssertBlockedWithoutEffectAsync("a digest reporting another size", badSize);

        var badDigest = new ScriptedResolver
        {
            Responder = request => new SyncAuthorityCommonDigest(request.RootBindingId, request.ObjectVersionId,
                request.Size, request.NativeAlgorithm, request.NativeHash, "not-a-digest", request.Size)
        };
        await h.AssertBlockedWithoutEffectAsync("an invalid SHA-256 digest", badDigest);

        var badAlgorithm = new ScriptedResolver
        {
            Responder = request => new SyncAuthorityCommonDigest(request.RootBindingId, request.ObjectVersionId,
                request.Size, SyncContentIdentity.Sha256, request.NativeHash,
                Sha256Identity("second cross revision"), request.Size)
        };
        await h.AssertBlockedWithoutEffectAsync("a digest describing another native algorithm", badAlgorithm);

        var badNative = new ScriptedResolver
        {
            Responder = request => new SyncAuthorityCommonDigest(request.RootBindingId, request.ObjectVersionId,
                request.Size, request.NativeAlgorithm, "0" + request.NativeHash[1..],
                Sha256Identity("second cross revision"), request.Size)
        };
        await h.AssertBlockedWithoutEffectAsync("a digest describing another native hash", badNative);

        var missing = new ScriptedResolver();
        await h.AssertBlockedWithoutEffectAsync("a resolver with no verified digest", missing);
        h.Assert(missing.Calls == 1, "the missing-proof case really asked the resolver");
    }

    private static async Task CacheReuseAsync(Harness h)
    {
        await h.WriteLocalAsync("cache.txt", "cache corpus");
        h.ScriptFile("cache.txt", "cache corpus", Sha256Identity("cache corpus"));
        await h.ObserveAsync();
        var local = (await h.ObservedAsync("local")).Single(item => item.RelativePath == "cache.txt");
        var remote = (await h.ObservedAsync("dropbox")).Single(item => item.RelativePath == "cache.txt");

        var resolver = new ScriptedResolver
        {
            Responder = request => Verified(request, Sha256Identity("cache corpus"))
        };
        var comparison = Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive, resolver);
        h.Assert((await comparison.CompareAsync(local, remote)).IsEqual,
            "the verified common digest proves equality");
        h.Assert(resolver.Calls == 1, "the first comparison resolved the incomparable side once");
        h.Assert((await comparison.CompareAsync(local, remote)).IsEqual,
            "the repeated exact key returns the same verified answer");
        h.Assert(resolver.Calls == 1, "the exact repeated key reuses the cached digest");
        h.Assert(comparison.CachedDigestCount == 1, "one exact key is retained");

        var revised = local with { ObjectVersionId = local.ObjectVersionId + "-rev2" };
        h.Assert((await comparison.CompareAsync(revised, remote)).IsEqual,
            "the revised identity is resolved and compared again");
        h.Assert(resolver.Calls == 2, "a changed revision invalidates the cached result");
        h.Assert(comparison.CachedDigestCount == 2, "the revised identity is cached under its own exact key");

        var bounded = SyncAuthorityProviderAwareContentComparison.ForAlgorithmNames(
            SyncContentIdentity.DropboxBlockSha256, SyncContentIdentity.Sha256, new ScriptedResolver
            {
                Responder = request => Verified(request, Sha256Identity("cache corpus"))
            }, 2);
        for (var index = 0; index < 4; index++)
            await bounded.CompareAsync(local with { ObjectVersionId = local.ObjectVersionId + "-" + index },
                remote);
        h.Assert(bounded.CachedDigestCount == 2, "the instance cache never exceeds its declared exact-key capacity");
        h.Assert(SyncAuthorityProviderAwareContentComparison.DefaultCacheCapacity == 1024,
            "the default instance bound is 1024 exact keys");
    }

    private static async Task NoStreamAsync(Harness h)
    {
        h.CreateLocalDirectory("folder");
        h.ScriptDirectory("folder");
        await h.WriteLocalAsync("folder/member.txt", "folder member");
        h.ScriptFile("folder/member.txt", "folder member", DropboxIdentity("folder member"));
        await h.WriteLocalAsync("sizes.txt", "short");
        h.ScriptFile("sizes.txt", "a much longer remote revision",
            DropboxIdentity("a much longer remote revision"));
        await h.WriteLocalAsync("plain.txt", "plain corpus");
        h.ScriptFile("plain.txt", "plain corpus", DropboxIdentity("plain corpus"));
        await h.ObserveAsync();

        var local = await h.ObservedAsync("local");
        var remote = await h.ObservedAsync("dropbox");
        var resolver = new ScriptedResolver();
        var comparison = Strict(SyncProviders.Dropbox, SyncProviders.Dropbox, resolver);
        var folderLeft = local.Single(item => item.RelativePath == "folder");
        var folderRight = remote.Single(item => item.RelativePath == "folder");
        h.Assert(folderLeft.ObjectKind == "directory" && folderRight.ObjectKind == "directory",
            "the fixture retains one directory on each side");
        h.Assert((await comparison.CompareAsync(folderLeft, folderRight)).IsEqual,
            "two directories are equal without opening any stream");
        var sizeLeft = local.Single(item => item.RelativePath == "sizes.txt");
        var sizeRight = remote.Single(item => item.RelativePath == "sizes.txt");
        h.Assert(sizeLeft.Size != sizeRight.Size, "the fixture retains different sizes");
        h.Assert((await comparison.CompareAsync(sizeLeft, sizeRight)).IsDifferent,
            "a retained size mismatch is a definite difference");
        var plainLeft = local.Single(item => item.RelativePath == "plain.txt");
        var plainRight = remote.Single(item => item.RelativePath == "plain.txt");
        // A same-path file/folder collision cannot even be retained: the observation path refuses the
        // kind collision for one deterministic object. The comparer therefore proves the kind branch with
        // two real retained entries, one directory and one byte file.
        h.Assert((await comparison.CompareAsync(folderLeft, plainRight)).IsDifferent,
            "a real directory entry compared with a real file entry is a definite difference");
        h.Assert((await comparison.CompareAsync(plainLeft, folderRight)).IsDifferent,
            "the reverse real kind pairing is also a definite difference");
        h.Assert(resolver.Calls == 0, "directory, kind and size decisions never invoke the digest resolver");

        var unregistered = SyncAuthorityProviderAwareContentComparison.ForAlgorithmNames(
            SyncContentIdentity.Unknown, SyncContentIdentity.Unknown, resolver);
        h.Assert((await unregistered.CompareAsync(plainLeft, plainRight)).IsUnknown,
            "an unregistered algorithm is never treated as a comparable identity");
        h.Assert(resolver.Calls == 0, "an unregistered algorithm invents no resolution capability");
    }

    private static async Task CancellationAsync(Harness h)
    {
        await h.WriteLocalAsync("cancel.txt", "first cancellation revision");
        h.ScriptFile("cancel.txt", "first cancellation revision",
            DropboxIdentity("first cancellation revision"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("cancel.txt", "second cancellation revision");
        h.ScriptFile("cancel.txt", "second cancellation revision",
            DropboxIdentity("second cancellation revision"));
        await h.ObserveAsync();

        using var cancellation = new CancellationTokenSource();
        var resolver = new ScriptedResolver
        {
            Responder = request =>
            {
                cancellation.Cancel();
                return Verified(request, Sha256Identity("second cancellation revision"));
            }
        };
        var cancelled = false;
        try
        {
            await new SyncAuthorityReconciliationPlanner(h.Db,
                    new SyncAuthorityPlannerOptions(1, "cc2a-profile", "cc2a-local-control",
                        "cc2a-cloud-control"), h.Admission,
                    contentComparison: Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive, resolver))
                .PlanAsync(h.Pair.Id, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        h.Assert(cancelled, "cancellation reaches the planner caller instead of continuing the comparison");
        h.Assert(resolver.Calls == 1, "the cancellation happened while the unverifiable side was resolving");
        h.Assert(await h.RunnableOperationsAsync() == 0, "a cancelled comparison plans no effect");
    }

    private static async Task NewPathUnknownAsync(Harness h)
    {
        await h.WriteLocalAsync("keep.txt", "kept baseline");
        h.ScriptFile("keep.txt", "kept baseline", DropboxIdentity("kept baseline"));
        await h.ObserveAsync();
        await h.AdoptInitialAsync(Strict(SyncProviders.Dropbox, SyncProviders.Dropbox));

        await h.WriteLocalAsync("new.txt", "new path corpus");
        h.ScriptFile("new.txt", "new path corpus", DropboxIdentity("new path corpus"));
        await h.ObserveAsync();
        var resolver = new ScriptedResolver();
        var outcomes = await h.ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive,
            resolver));

        h.Assert(outcomes.Single(item => item.RelativePath == "keep.txt").Kind ==
            SyncAuthorityPlannerOutcomeKind.Unchanged, "the retained baseline is unaffected");
        h.Assert(outcomes.Single(item => item.RelativePath == "new.txt").Kind ==
            SyncAuthorityPlannerOutcomeKind.Blocked,
            "a new path present on both sides with unknown equality blocks");
        h.Assert(resolver.Calls == 1, "the new path asked for exactly one common digest");
        h.Assert(await h.OpenConflictsAsync() == 0, "the new path was not turned into a definite conflict");
        h.Assert(await h.RunnableOperationsAsync() == 0, "the blocked new path plans no effect");
    }

    // ----- fixtures ------------------------------------------------------------------------------

    private static string DropboxIdentity(string text) =>
        SyncContentIdentity.Compute(SyncProviders.Dropbox, Encoding.UTF8.GetBytes(text));

    private static string Describe(IReadOnlyList<SyncDecision> decisions) =>
        string.Join(" | ", decisions.Select(item => item.RelativePath + "=" + item.Kind + ": " + item.Reason));

    private static string Sha256Identity(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static SyncAuthorityCommonDigest Verified(SyncAuthorityCommonDigestRequest request, string sha256) =>
        new(request.RootBindingId, request.ObjectVersionId, request.Size, request.NativeAlgorithm,
            request.NativeHash, sha256, request.Size);

    private static SyncAuthorityProviderAwareContentComparison Strict(string? leftProvider,
        string? rightProvider, ISyncAuthorityCommonDigestResolver? resolver = null) =>
        new(leftProvider, rightProvider, resolver);

    private sealed class ScriptedResolver : ISyncAuthorityCommonDigestResolver
    {
        internal int Calls;
        internal Func<SyncAuthorityCommonDigestRequest, SyncAuthorityCommonDigest?>? Responder;

        public Task<SyncAuthorityCommonDigest?> ResolveAsync(SyncAuthorityCommonDigestRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Responder is null ? null : Responder(request));
        }
    }

    public class ScriptedRemote : DispatchProxy
    {
        internal IReadOnlyList<PilotRemoteEntry> Entries { get; set; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Task.CompletedTask,
            nameof(ISyncRemoteStore.ListEntriesAsync) => Task.FromResult(Entries),
            _ => throw new InvalidOperationException(
                "Unexpected provider effect in the comparison-only fixture: " + method?.Name)
        };
    }

    private sealed class Harness
    {
        internal SyncPair Pair = null!;
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal string Local = null!;
        internal readonly List<PilotRemoteEntry> Remote = [];
        internal int Assertions;

        internal static async Task<Harness> CreateAsync(string root, string id, string? pairId = null)
        {
            var caseRoot = Path.Combine(root, id);
            var local = Path.Combine(caseRoot, "local");
            Directory.CreateDirectory(local);
            PilotDirectoryIdentity identity;
            using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
            var pair = new SyncPair(pairId ?? "cc2a-" + id, "CC2A " + id, local, identity.VolumeSerialNumber,
                identity.FileId, "/Remote", "id:remote-root", Provider: SyncProviders.Dropbox);
            var db = new SyncAuthorityDatabase(Path.Combine(caseRoot, "authority.db"));
            await db.InitializeAsync("cc2a-install", "cc2a-profile", "cc2a-check", "{}");
            await db.RegisterPairAsync(pair.Id, 1);
            await db.RegisterRootBindingAsync("cc2a-local", pair.Id, "local", 1,
                JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), local);
            await db.RegisterRootBindingAsync("cc2a-cloud", pair.Id, "dropbox", 1,
                "{\"folderId\":\"id:remote-root\"}", pair.DropboxRoot);
            await db.VerifyHealthAsync();
            var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
                null, null, JsonSerializer.Serialize(new { pairIds = new[] { pair.Id } }),
                SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            return new Harness { Pair = pair, Db = db, Local = local, Admission = admission };
        }

        internal async Task WriteLocalAsync(string relativePath, string content)
        {
            var full = Path.Combine(Local, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        internal void CreateLocalDirectory(string relativePath) =>
            Directory.CreateDirectory(Path.Combine(Local,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        internal void ScriptFile(string relativePath, string content, string? contentHash)
        {
            // The scripted store is the remote's current state, so re-scripting a path replaces it.
            Remote.RemoveAll(item => string.Equals(item.RelativePath, relativePath,
                StringComparison.OrdinalIgnoreCase));
            Remote.Add(new PilotRemoteEntry(relativePath, PilotEntryKind.File, "id:" + relativePath,
                "rev:" + relativePath + ":" + (contentHash is null ? "nohash" : contentHash),
                Encoding.UTF8.GetByteCount(content), contentHash));
        }

        internal void ScriptDirectory(string relativePath)
        {
            Remote.RemoveAll(item => string.Equals(item.RelativePath, relativePath,
                StringComparison.OrdinalIgnoreCase));
            Remote.Add(new PilotRemoteEntry(relativePath, PilotEntryKind.Directory, "id:" + relativePath,
                null, 0, null));
        }

        internal async Task ObserveAsync()
        {
            var remote = DispatchProxy.Create<ISyncRemoteStore, ScriptedRemote>();
            ((ScriptedRemote)(object)remote).Entries = Remote.ToArray();
            var observer = new SyncAuthorityObservationCoordinator(Pair, remote, Db, "cc2a-local",
                "cc2a-cloud");
            await observer.ObserveAsync("initial");
            await observer.ObserveAsync("initial");
        }

        internal async Task<IReadOnlyList<SyncDecision>> AdoptInitialAsync(
            ISyncAuthorityContentComparison? comparison = null) =>
            await new SyncAuthorityInitialPlanner(Db, comparison).AdoptAsync(Pair.Id);

        internal async Task<IReadOnlyList<SyncAuthorityPlannerOutcome>> ReconcileAsync(
            ISyncAuthorityContentComparison? comparison = null) =>
            await new SyncAuthorityReconciliationPlanner(Db,
                    new SyncAuthorityPlannerOptions(1, "cc2a-profile", "cc2a-local-control",
                        "cc2a-cloud-control"), Admission, contentComparison: comparison)
                .PlanAsync(Pair.Id);

        internal async Task AssertBlockedWithoutEffectAsync(string label,
            ISyncAuthorityCommonDigestResolver resolver)
        {
            await ObserveAsync();
            var outcomes = await ReconcileAsync(Strict(SyncProviders.Dropbox, SyncProviders.GoogleDrive,
                resolver));
            Assert(outcomes.Single().Kind == SyncAuthorityPlannerOutcomeKind.Blocked,
                label + " leaves the comparison Unknown and the decision Blocked");
            Assert(await RunnableOperationsAsync() == 0, label + " plans no operation");
            Assert(await OpenConflictsAsync() == 0, label + " claims no definite conflict");
        }

        internal async Task<IReadOnlyList<SyncAuthorityObservedEntry>> ObservedAsync(string side) =>
            await Db.LoadLatestCompleteObservationAsync(Pair.Id, side);

        internal async Task<int> RunnableOperationsAsync() =>
            (await Db.ListRunnableOperationsForPairAsync(Pair.Id)).Count;

        internal async Task<int> OpenConflictsAsync() =>
            (await Db.LoadOpenConflictIdsAsync(Pair.Id)).Count;

        internal async Task<int> SyncPointsAsync() =>
            (await Db.LoadLatestSynchronizationPointsAsync(Pair.Id)).Count;

        internal void Assert(bool condition, string label)
        {
            Assertions++;
            if (!condition) throw new InvalidDataException("CC2A assertion failed: " + label);
        }
    }
}
