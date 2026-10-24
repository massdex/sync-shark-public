using Massdex.SyncShark;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Text;

internal static class IncrementalInventoryChecks
{
    public static async Task RunAsync(string folder)
    {
        var root = Path.Combine(folder, "local"); Directory.CreateDirectory(root);
        for (var index = 0; index < 120; index++)
            await File.WriteAllTextAsync(Path.Combine(root, $"file-{index:000}.txt"), "original-" + index);
        using var lease = PilotDirectoryLease.Open(root);
        var pair = new SyncPair("incremental-check", "Incremental", root, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Incremental", "id:incremental");
        var first = await SyncLocalInventoryIndex.ScanAsync(pair, lease, "initial", null);
        Require(!first.Incremental && first.Entries.Count == 120, "Initial held-root inventory is complete");
        var probePath = "file-004.txt";
        var entry = first.Entries[probePath];
        var unknown = new NtfsUsnChange(0xf000000000000001, 0xf000000000000002, 1, 1, FileAttributes.Normal, "unrelated.txt");
        Require(SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, first, [unknown])!.Count == 0,
            "Unrelated drive changes do not dirty this pair");
        var changed = new NtfsUsnChange(entry.FileId!.Value, pair.LocalRootFileId, 2, 1, FileAttributes.Normal, probePath);
        Require(SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, first, [changed])!.SequenceEqual([probePath]),
            "Changed NTFS identity resolves to one file, not the whole pair");
        var rename = changed with { Name = "renamed.txt", Reason = 0x2000 };
        var renamedPaths = SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, first, [rename])!;
        Require(renamedPaths.Count == 2 && renamedPaths.Contains(probePath) && renamedPaths.Contains("renamed.txt"),
            "Rename retains both old and new path scopes");
        Require(SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, first,
            [changed with { FileId = pair.LocalRootFileId, Reason = 0x200 }]) is null,
            "Root structural uncertainty forces full reconciliation");
        var name = Encoding.Unicode.GetBytes("one.txt"); var record = new byte[60 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 0x1234000000000010);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 0x5678000000000020);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), 30);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60); name.CopyTo(record, 60);
        Require(NtfsUsnJournal.ParseChanges(record, 0, 31).Single().FileId == 0x1234000000000010,
            "Journal decoding preserves the full identity including NTFS sequence bits");
        try { NtfsUsnJournal.ParseChanges(record.AsSpan(0, 59), 0, 31); throw new Exception("Malformed record accepted"); }
        catch (IOException) { Console.WriteLine("PASS Truncated journal record is rejected"); }
        await File.WriteAllTextAsync(Path.Combine(root, probePath), "changed-content");
        var second = await SyncLocalInventoryIndex.ScanAsync(pair, lease, "monitor", null);
        Require(second.Entries[probePath].ContentHash != first.Entries[probePath].ContentHash,
            "Actual file change is rehashed and reaches the authoritative inventory");
        if (second.Incremental)
            Require(second.PathsInspected < first.PathsInspected && second.FilesHashed == 1,
                $"Real native refresh inspected {second.PathsInspected} paths and hashed {second.FilesHashed} file instead of {first.PathsInspected}");
        else Console.WriteLine("INFO Native journal fallback: " + (NtfsUsnJournal.LastReadFailure ?? "scope continuity requires complete scan"));
        var idle = await SyncLocalInventoryIndex.ScanAsync(pair, lease, "monitor", null);
        Require(idle.Incremental && idle.FilesHashed == 0 && idle.PathsInspected == 0 &&
            ReferenceEquals(idle.Entries, second.Entries), "A real unchanged-folder check reuses its inventory without scanning, hashing or rebuilding the entry map");
        // Exercise the real scoped scanner and merge on this filesystem regardless of volume privileges.
        var partial = await new PilotLocalScanner().ScanAsync(lease, refreshPaths: [probePath]);
        var merged = PilotLocalScanner.MergeIncremental(first, partial, [probePath]);
        Require(partial.PathsInspected == 1 && partial.FilesHashed == 1 && merged.Entries.Count == 120 &&
                merged.Entries[probePath].ContentHash == second.Entries[probePath].ContentHash,
            "Production scoped scanner refreshes one file and preserves the other 119 entries");
        File.Delete(Path.Combine(root, probePath));
        var deletion = await new PilotLocalScanner().ScanAsync(lease, refreshPaths: [probePath]);
        Require(!PilotLocalScanner.MergeIncremental(second, deletion, [probePath]).Entries.ContainsKey(probePath),
            "Journal-bound deleted path is removed without discarding unrelated inventory");
        var full = await SyncLocalInventoryIndex.ScanAsync(pair, lease, "safety-check", null);
        Require(!full.Incremental && full.Entries.Count == 119, "Safety check re-establishes complete inventory");
        var newPath = Path.Combine(root, "discovered.txt");
        await File.WriteAllTextAsync(newPath, "a newly discovered file");
        var newIdentity = WindowsCloudFilesProvider.ReadStorageFileIdentity(newPath, pair.Id);
        var hint = new SyncDiscoveryItem("discovered.txt", newIdentity.LocalId, pair.LocalRootFileId, 50,
            FileAttributes.Normal, 1, new FileInfo(newPath).Length, File.GetLastWriteTimeUtc(newPath));
        var nameless = new NtfsUsnChange(hint.FileId, pair.LocalRootFileId, 49, 0x100, FileAttributes.Normal, "");
        var hints = new Dictionary<ulong, SyncDiscoveryItem> { [hint.FileId] = hint };
        Require(SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, full, [nameless]) is null,
            "A nameless new root item requires ordinary reconciliation without Discovery");
        var resolved = SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, full, [nameless], hints);
        Require(resolved is not null && resolved.SequenceEqual(["discovered.txt"]),
            "Production resolver consumes the bound discovery hint as one scanner target");
        var discovered = await new PilotLocalScanner().ScanAsync(lease, refreshPaths: resolved);
        var combined = PilotLocalScanner.MergeIncremental(full, discovered, resolved!);
        Require(discovered.PathsInspected == 1 && discovered.FilesHashed == 1 && combined.Entries.Count == 120 &&
                combined.Entries["discovered.txt"].ContentHash is not null,
            "The real ordinary scanner verifies Discovery's new file without rehashing the other 119 files");
        Require(SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, full, [nameless with { Usn = 51 }], hints) is null,
            "A newer journal change cannot reuse an older Discovery hint");
        hints[hint.FileId] = hint with { LinkCount = 2 };
        Require(SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, full, [nameless], hints) is null,
            "A multi-link result requires full ordinary inspection");
        Require(SyncLocalInventoryIndex.ResolveChangedPaths(pair.LocalRootFileId, full,
                [unknown with { Reason = 0x10000 }]) is null,
            "An outside MFT parent does not conceal an in-root hard-link change");
        File.Delete(newPath);
        Console.WriteLine($"Measured ordinary initial scan: {first.PathsInspected} paths, {first.FilesHashed} hashes, {first.Elapsed?.TotalMilliseconds:F1} ms. " +
            $"Changed scan: {second.PathsInspected} paths, {second.FilesHashed} hashes, {second.Elapsed?.TotalMilliseconds:F1} ms. " +
            $"Idle check: {idle.PathsInspected} paths, {idle.FilesHashed} hashes, {idle.Elapsed?.TotalMilliseconds:F1} ms. " +
            "Discovery hint input was synthetic; this is not an elevated-MFT or Dropbox speed comparison.");
        MergeScopeEquivalence();
        MergeScaleCheck();
        await CancellationBoundaryAsync(folder);
        await ProviderRebindAsync(folder);
    }

    // The replaced production predicate, kept verbatim as the reference the merge must reproduce:
    // paths.Any(path => SyncDirectoryStoragePolicy.Contains(path, entry.RelativePath)).
    private static bool ReferenceRefreshed(IReadOnlyList<string> scope, string path) =>
        scope.Any(candidate => SyncDirectoryStoragePolicy.Contains(candidate, path));

    private static PilotLocalEntry Entry(string relativePath) => new(relativePath, PilotEntryKind.File,
        relativePath, 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null);

    private static PilotLocalScan ScanOf(params string[] relativePaths)
    {
        var entries = new Dictionary<string, PilotLocalEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths) entries.Add(path, Entry(path));
        return new PilotLocalScan(entries, [], relativePaths.Length);
    }

    private static SyncPair Pair(string id, string name, PilotDirectoryLease lease, string provider) =>
        new(id, name, lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/" + name,
            "id:" + id, ConnectionId: id, Provider: provider);

    // Ancestor, descendant, sibling-prefix, case, trailing-slash, nonASCII, empty and exact-equal scope
    // decisions must each keep exactly the reference Contains decision.
    private static void MergeScopeEquivalence()
    {
        IReadOnlyList<string>[] scopes =
        [
            ["dir"], ["dir/sub"], ["dir/"], ["café"], ["CAFÉ"], ["a/b"], [], ["top.txt"], ["dir/CHILD.TXT"]
        ];
        string[] candidates =
        [
            "top.txt", "dir", "dir/", "dir/child.txt", "dir/sub", "dir/sub/deep.txt", "dirbar/other.txt",
            "dird/other.txt", "DIFFERENT/child.txt", "dir/CHILD.TXT", "café/naïve.txt", "a/b/c"
        ];
        var mismatches = new List<string>();
        foreach (var scope in scopes)
            foreach (var candidate in candidates)
            {
                var merged = PilotLocalScanner.MergeIncremental(ScanOf(candidate), ScanOf(), scope);
                var reference = ReferenceRefreshed(scope, candidate);
                if (reference == merged.Entries.ContainsKey(candidate))
                    mismatches.Add("[" + string.Join("|", scope) + "] '" + candidate + "' reference=" + reference +
                        " production=" + !merged.Entries.ContainsKey(candidate));
            }
        Require(mismatches.Count == 0, "All " + scopes.Length * candidates.Length +
            " supplied-scope cases keep the exact reference Contains decision" +
            (mismatches.Count == 0 ? string.Empty : "; mismatches: " + string.Join("; ", mismatches)));

        // An empty supplied path is an ancestor only for an empty entry or an entry that begins with '/'.
        string[] emptyScope = [""];
        Require(ReferenceRefreshed(emptyScope, "") && ReferenceRefreshed(emptyScope, "/x") &&
                !ReferenceRefreshed(emptyScope, "plain.txt"),
            "The reference predicate treats an empty scope as matching only the empty and initial-slash paths");
        Require(PilotLocalScanner.MergeIncremental(ScanOf("plain.txt"), ScanOf(), emptyScope).Entries.ContainsKey("plain.txt") &&
                PilotLocalScanner.MergeIncremental(ScanOf(""), ScanOf(), emptyScope).Entries.Count == 0 &&
                PilotLocalScanner.MergeIncremental(ScanOf("/x"), ScanOf(), emptyScope).Entries.Count == 0 &&
                PilotLocalScanner.MergeIncremental(ScanOf("plain.txt"), ScanOf(), []).Entries.ContainsKey("plain.txt"),
            "The empty scope refreshes exactly the empty and initial-slash entries and never an ordinary contained path");

        // A subtree root, its nested descendant, a deleted path and a genuine canonical collision in one merge.
        string[] treeScope = ["Folder", "removed.txt"];
        var retainedTree = ScanOf("Folder", "Folder/child.txt", "Folder/sub/deep.txt", "Folderish/other.txt",
            "Café.txt", "Cafe\u0301.txt", "deleted.txt", "removed.txt");
        var mergedTree = PilotLocalScanner.MergeIncremental(retainedTree, ScanOf(), treeScope);
        var referenceSurvivors = retainedTree.Entries.Values
            .Where(entry => !ReferenceRefreshed(treeScope, entry.RelativePath))
            .Select(entry => entry.RelativePath).ToArray();
        Require(referenceSurvivors.Length == 4 && mergedTree.Entries.Count == 2 &&
                mergedTree.Entries.ContainsKey("Folderish/other.txt") && mergedTree.Entries.ContainsKey("deleted.txt") &&
                referenceSurvivors.Where(path => path is not ("Café.txt" or "Cafe\u0301.txt"))
                    .All(path => mergedTree.Entries.ContainsKey(path)),
            "A subtree root, its nested descendant and the deleted path are refreshed while every unrelated reference survivor is kept");
        Require(mergedTree.Decisions.Count(decision => decision.Kind == PilotDecisionKind.Blocked) == 2 &&
                mergedTree.Decisions.Any(decision => decision.RelativePath == "Café.txt") &&
                mergedTree.Decisions.Any(decision => decision.RelativePath == "Cafe\u0301.txt"),
            "The unchanged collision handling still blocks both NFC-equivalent names with two explicit decisions");
        var thirdAlias = PilotLocalScanner.MergeIncremental(retainedTree, ScanOf("CAFÉ.txt"), treeScope);
        Require(!thirdAlias.Entries.Keys.Any(path => PilotPath.CanonicalComparisonKey(path) ==
                    PilotPath.CanonicalComparisonKey("Café.txt")) &&
                thirdAlias.Decisions.Count(decision => decision.Kind == PilotDecisionKind.Blocked) == 3,
            "A third alias stays blocked after both original collision spellings are removed");
        Require(mergedTree.Entries.Keys.Contains("Folderish/other.txt") &&
                mergedTree.Entries.ContainsKey("FOLDERISH/OTHER.TXT"),
            "Published scan keys retain original spelling and case-insensitive lookup");
        var refreshedCopy = ScanOf("Folder/child.txt");
        var mergedTreeRefreshed = PilotLocalScanner.MergeIncremental(retainedTree, refreshedCopy, treeScope);
        Require(mergedTreeRefreshed.Entries.Count == 3 && ReferenceEquals(mergedTreeRefreshed.Entries["Folder/child.txt"],
                refreshedCopy.Entries["Folder/child.txt"]),
            "A refreshed descendant replaces its refreshed retained copy with the refreshed entry while the other survivors stay unchanged");
    }

    // 190000 retained entries against 10000 supplied changed paths. The production merge result is compared
    // with a constructive expectation from the fixture layout plus a bounded sample of the real reference
    // predicate; the measured scope-lookup counter and wall time are recorded, and the reference predicate's
    // 1.9-billion-pair arithmetic is reported without being executed.
    private static void MergeScaleCheck()
    {
        const int directoryCount = 1900;
        const int filesPerDirectory = 100;
        const int retainedCount = directoryCount * filesPerDirectory;
        const string sharedHash = "0000000000000000000000000000000000000000000000000000000000000000";
        var write = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = new Dictionary<string, PilotLocalEntry>(retainedCount, StringComparer.OrdinalIgnoreCase);
        var retainedPaths = new string[retainedCount];
        var next = 0;
        for (var directory = 0; directory < directoryCount; directory++)
            for (var file = 0; file < filesPerDirectory; file++)
            {
                var entryPath = $"d-{directory:D4}/f-{file:D3}.txt";
                entries.Add(entryPath, new PilotLocalEntry(entryPath, PilotEntryKind.File, entryPath, 1, write, sharedHash));
                retainedPaths[next++] = entryPath;
            }
        var changed = new List<string>(10000);
        for (var directory = 0; directory < 100; directory++) changed.Add($"d-{directory:D4}");
        for (var directory = 100; directory < 200; directory++)
            for (var file = 0; file < 99; file++) changed.Add(retainedPaths[directory * filesPerDirectory + file]);
        Require(retainedCount == 190000 && changed.Count == 10000,
            "The scale fixture binds 190000 synthetic retained entries and 10000 supplied changed paths");
        var changedSet = new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase);
        var exactMatches = retainedPaths.Count(candidate => changedSet.Contains(candidate));
        var expectedLookups = exactMatches + 2L * (retainedCount - exactMatches);
        var watch = Stopwatch.StartNew();
        var merged = PilotLocalScanner.MergeIncremental(new PilotLocalScan(entries, [], retainedCount),
            ScanOf(), changed, CancellationToken.None, out var lookups);
        watch.Stop();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var directory = 100; directory < directoryCount; directory++)
            for (var file = directory < 200 ? 99 : 0; file < filesPerDirectory; file++)
                expected.Add(retainedPaths[directory * filesPerDirectory + file]);
        var observed = new HashSet<string>(merged.Entries.Keys, StringComparer.OrdinalIgnoreCase);
        Require(observed.SetEquals(expected) && expected.Count == retainedCount - 100 * filesPerDirectory - 9900,
            "The 190000-entry merge keeps exactly " + expected.Count +
            " unrelated retained entries, identical to the expectation built from the fixture layout");
        var sampleMismatches = 0;
        for (var index = 0; index < retainedPaths.Length; index += 1900)
            if (ReferenceRefreshed(changed, retainedPaths[index]) == observed.Contains(retainedPaths[index]))
                sampleMismatches++;
        Require(sampleMismatches == 0,
            "A bounded sample of " + (retainedPaths.Length / 1900) +
            " retained entries at full scale still matches the real reference Contains predicate exactly");
        Require(lookups == expectedLookups && lookups < 2L * retainedCount,
            "The production scope predicate performed " + lookups + " exact-string lookups instead of the reference's " +
            retainedCount + " x " + changed.Count + " predicate calls");
        Console.WriteLine($"Measured merge scope: {retainedCount} retained entries, {changed.Count} supplied paths, " +
            $"{lookups} production scope lookups, {watch.Elapsed.TotalMilliseconds:F1} ms, reference predicate " +
            $"arithmetic {(long)retainedCount * changed.Count:N0} pair calls deliberately not executed, " +
            $"{exactMatches} exact-path matches, {expected.Count} reused entries. " +
            "This is a synthetic in-memory merge, not a provider or elevated-MFT benchmark.");
    }

    private static async Task CancellationBoundaryAsync(string folder)
    {
        var many = new Dictionary<string, PilotLocalEntry>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 1000; index++)
        {
            var relative = $"cancelled/f-{index:D4}.txt";
            many.Add(relative, Entry(relative));
        }
        using var midMerge = new CancellationTokenSource();
        var aborted = false;
        try
        {
            PilotLocalScanner.MergeIncremental(new PilotLocalScan(new CancellingEntries(many, midMerge, 300), [], 1000),
                ScanOf(), ["cancelled/absent.txt"], midMerge.Token, out _);
        }
        catch (OperationCanceledException) { aborted = true; }
        Require(aborted, "A token cancelled inside the merge loop aborts the merge instead of publishing a partial inventory");
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            var refused = false;
            try
            {
                PilotLocalScanner.MergeIncremental(ScanOf("cancelled/f-0000.txt"), ScanOf(),
                    ["cancelled/absent.txt"], alreadyCancelled.Token, out _);
            }
            catch (OperationCanceledException) { refused = true; }
            Require(refused, "An already-cancelled token refuses the merge before any inventory is constructed");
        }

        // The same boundary through the real index: a cancelled attempt must not publish a partial index.
        var root = Path.Combine(folder, "cancel-index");
        Directory.CreateDirectory(root);
        for (var index = 0; index < 200; index++)
            await File.WriteAllTextAsync(Path.Combine(root, $"keep-{index:000}.txt"), "content-" + index);
        using var lease = PilotDirectoryLease.Open(root);
        var pair = Pair("incremental-cancel", "Incremental cancel", lease, SyncProviders.Dropbox);
        var checkpoint = new NtfsUsnCheckpoint(11, 1, 2, 1);
        IReadOnlyList<NtfsUsnChange> changes = [];
        var journal = new SyncLocalInventoryIndex.JournalSource(_ => checkpoint, (_, _, _, _) => changes);
        var complete = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "initial", null, journal);
        Require(!complete.Incremental && complete.Entries.Count == 200 && complete.Decisions.Count == 0,
            "The cancellation fixture first publishes one complete retained index");
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            var refused = false;
            try { await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "monitor", null, journal, alreadyCancelled.Token); }
            catch (OperationCanceledException) { refused = true; }
            Require(refused, "An already-cancelled token aborts the real index scan without publishing anything");
        }
        using var mergeCancellation = new CancellationTokenSource();
        changes = [new NtfsUsnChange(complete.Entries["keep-000.txt"].FileId!.Value, pair.LocalRootFileId,
            3, 1, FileAttributes.Normal, "keep-000.txt")];
        var refreshAborted = false;
        try
        {
            await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "monitor",
                (inventoryPath, _) =>
                {
                    if (inventoryPath == "Refreshing Changed Items") mergeCancellation.Cancel();
                    return Task.CompletedTask;
                }, journal, mergeCancellation.Token);
        }
        catch (OperationCanceledException) { refreshAborted = true; }
        Require(refreshAborted, "A cancelled incremental refresh aborts the real index scan before any merge is published");
        changes = [];
        var afterCancellation = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "monitor", null, journal);
        Require(afterCancellation.Incremental && afterCancellation.FilesHashed == 0 && afterCancellation.PathsInspected == 0 &&
                ReferenceEquals(afterCancellation.Entries, complete.Entries),
            "After both cancelled attempts the published index is still exactly the complete retained inventory");
    }

    private static async Task ProviderRebindAsync(string folder)
    {
        var root = Path.Combine(folder, "rebind");
        Directory.CreateDirectory(root);
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        await File.WriteAllBytesAsync(Path.Combine(root, "payload.bin"), bytes);
        using var lease = PilotDirectoryLease.Open(root);
        var dropbox = Pair("incremental-rebind", "Incremental rebind", lease, SyncProviders.Dropbox);
        var checkpoint = new NtfsUsnCheckpoint(23, 1, 2, 1);
        IReadOnlyList<NtfsUsnChange> changes = [];
        var journal = new SyncLocalInventoryIndex.JournalSource(_ => checkpoint, (_, _, _, _) => changes);
        var dropboxDigest = SyncContentIdentity.Compute(dropbox.Provider, bytes);
        var dropboxFirst = await SyncLocalInventoryIndex.ScanWithJournalAsync(dropbox, lease, "initial", null, journal);
        Require(!dropboxFirst.Incremental && dropboxFirst.FilesHashed == 1 &&
                dropboxFirst.Entries["payload.bin"].ContentHash == dropboxDigest && dropboxDigest == SyncContentIdentity.Compute(null, bytes),
            "A complete Dropbox index retains the exact registered block-hash algorithm digest for its provider");
        var dropboxIdle = await SyncLocalInventoryIndex.ScanWithJournalAsync(dropbox, lease, "monitor", null, journal);
        Require(dropboxIdle.Incremental && dropboxIdle.FilesHashed == 0 && dropboxIdle.PathsInspected == 0,
            "An unchanged same-pair same-policy view keeps reusing its index without rehashing anything");
        var google = dropbox with { Id = "incremental-rebind-google", Provider = SyncProviders.GoogleDrive };
        var googleDigest = SyncContentIdentity.Compute(SyncProviders.GoogleDrive, bytes);
        var googleFirst = await SyncLocalInventoryIndex.ScanWithJournalAsync(google, lease, "monitor", null, journal);
        Require(!googleFirst.Incremental && googleFirst.FilesHashed == 1 && googleFirst.PathsInspected == 1 &&
                googleFirst.Entries["payload.bin"].ContentHash == googleDigest && googleDigest != dropboxDigest,
            "An owning-pair and provider rebind on the shared root forces a full held-byte rescan with the newly registered algorithm");
        var rebound = await SyncLocalInventoryIndex.ScanWithJournalAsync(dropbox, lease, "monitor", null, journal);
        Require(!rebound.Incremental && rebound.FilesHashed == 1 &&
                rebound.Entries["payload.bin"].ContentHash == dropboxDigest,
            "Rebinding back to the original pair again forces the full rescan instead of reusing the other binding's hashes");
        var reboundIdle = await SyncLocalInventoryIndex.ScanWithJournalAsync(dropbox, lease, "monitor", null, journal);
        Require(reboundIdle.Incremental && reboundIdle.FilesHashed == 0,
            "The original pair's continuous reuse resumes once its own binding is the current policy again");
    }

    /// <summary>A retained view whose token is cancelled only after the merge has already processed entries,
    /// so the in-loop cancellation check - not the pre-construction check - is what aborts the merge.</summary>
    private sealed class CancellingEntries(Dictionary<string, PilotLocalEntry> inner,
        CancellationTokenSource cancellation, int cancelAfterIndex) : IReadOnlyDictionary<string, PilotLocalEntry>
    {
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<PilotLocalEntry> Values => Enumerate();
        public int Count => inner.Count;
        public PilotLocalEntry this[string key] => inner[key];
        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public bool TryGetValue(string key, out PilotLocalEntry value) => inner.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, PilotLocalEntry>> GetEnumerator() => inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => inner.GetEnumerator();

        private IEnumerable<PilotLocalEntry> Enumerate()
        {
            var yielded = 0;
            foreach (var entry in inner.Values)
            {
                if (yielded++ == cancelAfterIndex) cancellation.Cancel();
                yield return entry;
            }
        }
    }

    private static void Require(bool pass, string label)
    { if (!pass) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
}
