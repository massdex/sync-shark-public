using Massdex.SyncShark;

// RTS checks: completed transfer totals are reported per direction and are independent of the
// final most-material per-path decision. Authored checks are not executed evidence until run.
internal static class RunTransferSummaryChecks
{
    private static int _assertions;

    internal static Task RunAsync(string root)
    {
        Directory.CreateDirectory(Path.GetFullPath(root));
        _assertions = 0;
        Console.WriteLine("RTS run transfer summary route entered");
        LegacyFallbackIsUnchanged();
        SnapshotReportsEachDirectionOnce();
        var frozen = new SyncRunTransferTotals(new[] { "one" }, Array.Empty<string>());
        var mutationRejected = false;
        try { ((ISet<string>)frozen.UploadedPaths).Add("not allowed"); }
        catch (NotSupportedException) { mutationRejected = true; }
        Assert(mutationRejected && frozen.Uploaded == 1, "published set rejects mutation even through ISet");
        DuplicateSameDirectionCountedOnce();
        MutatingCallerSetsCannotChangePublishedTotals();
        Console.WriteLine("PASS run transfer summary: " + _assertions + " assertions");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static void LegacyFallbackIsUnchanged()
    {
        // The retained reduction keeps one decision per relative path, so a path uploaded and
        // then downloaded loses its upload decision. Without a snapshot the counters report
        // that loss, which is exactly the defect a completed-transfer snapshot corrects.
        var legacy = new SyncRunResult("pair-1", new[]
        {
            new SyncDecision("newfile", SyncDecisionKind.Downloaded, "downloaded"),
            new SyncDecision("existingfile", SyncDecisionKind.Downloaded, "downloaded")
        });
        Assert(legacy.Uploaded == 0 && legacy.Downloaded == 2,
            "legacy construction still counts the retained per-path decisions: uploaded 0, downloaded 2");
    }

    private static void SnapshotReportsEachDirectionOnce()
    {
        var totals = new SyncRunTransferTotals(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newfile" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newfile", "existingfile" });
        var result = new SyncRunResult("pair-1", new[]
        {
            new SyncDecision("newfile", SyncDecisionKind.Downloaded, "downloaded"),
            new SyncDecision("existingfile", SyncDecisionKind.Downloaded, "downloaded")
        })
        {
            TransferTotals = totals
        };
        Assert(result.Uploaded == 1 && result.Downloaded == 2,
            "a path uploaded then downloaded counts once in each direction: uploaded 1, downloaded 2");
        Assert(result.Decisions.Single(item => item.RelativePath == "newfile").Kind == SyncDecisionKind.Downloaded,
            "the retained most-material per-path decision is unchanged by the snapshot");
    }

    private static void DuplicateSameDirectionCountedOnce()
    {
        var totals = new SyncRunTransferTotals(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dup", "DUP" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var result = new SyncRunResult("pair-1", Array.Empty<SyncDecision>())
        {
            TransferTotals = totals
        };
        Assert(result.Uploaded == 1 && result.Downloaded == 0,
            "duplicate same-direction observations do not inflate the uploaded total");
    }

    private static void MutatingCallerSetsCannotChangePublishedTotals()
    {
        // The snapshot must own frozen copies: a caller that keeps mutating the sets it passed
        // in must not be able to change already published totals.
        var uploaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "first" };
        var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "first" };
        var totals = new SyncRunTransferTotals(uploaded, downloaded);
        var result = new SyncRunResult("pair-1", Array.Empty<SyncDecision>())
        {
            TransferTotals = totals
        };
        Assert(result.Uploaded == 1 && result.Downloaded == 1,
            "published totals start at uploaded 1 and downloaded 1");
        uploaded.Add("second");
        uploaded.Clear();
        downloaded.Add("second");
        Assert(result.Uploaded == 1 && result.Downloaded == 1,
            "mutating the caller sets afterwards cannot change the published totals");
        Assert(totals.UploadedPaths.Count == 1 && totals.DownloadedPaths.Count == 1,
            "the snapshot keeps its own owned copies rather than the caller collections");
    }
}
