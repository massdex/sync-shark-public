namespace Massdex.SyncShark.V2;

public sealed partial class ProductionEndpoints
{
    internal const int IncludePreviewEntryLimit = 10_000;
    internal const int IncludePreviewExampleLimit = 20;

    /// <summary>
    /// RQ-021/034/036/040: a scoped, read-only hypothetical newest-wins reinclude review.
    /// Does not open writers, update observations, inspect deletion history, or change pair policy.
    /// </summary>
    public async Task<SyncCliIncludePreviewData> PreviewIncludeAsync(PairDef pair, string relative,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var prefix = PilotPath.NormalizeRelative(relative);
        var scopes = pair.Excluded.Select(PilotPath.NormalizeRelative)
            .Where(path => Under(prefix, path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        scopes = scopes.Where(path => !scopes.Any(parent =>
            !string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) && Under(parent, path))).ToArray();
        var owned = ProductOwnedRelative(pair);
        var afterInclude = pair with { Excluded = pair.Excluded.Where(path => !Under(prefix, path)).ToArray() };
        var pairs = _store.Pairs(); // Read only: nested-pair ownership remains protected after inclusion.
        var includeA = PairInclude.For(afterInclude, Side.A, pairs, owned);
        var includeB = PairInclude.For(afterInclude, Side.B, pairs, owned);
        // Task.Run also lets synchronous local metadata enumeration overlap the other endpoint (decision 72).
        var a = Task.Run(() => ReadIncludePreviewScopesAsync(pair.A, scopes, owned, ct), ct);
        var b = Task.Run(() => ReadIncludePreviewScopesAsync(pair.B, scopes, owned, ct), ct);
        await Task.WhenAll(a, b).ConfigureAwait(false);
        static PreviewSide Protect(PreviewSide scan, Func<string, bool> include) => scan with
        { Entries = scan.Entries.Select(entry => entry with { Held = entry.Held || !include(entry.Path) }).ToArray() };
        return SummarizeIncludePreview(pair.PairId, prefix, Protect(await a, includeA), Protect(await b, includeB),
            PairInclude.IgnoreRules(pair));
    }

    internal sealed record PreviewEntry(string Path, PilotEntryKind Kind, long Size, string? Hash, bool Held,
        DateTimeOffset? ModifiedUtc = null);
    internal sealed record PreviewSide(IReadOnlyList<PreviewEntry> Entries, bool Complete, string? Warning = null);

    private static string PreviewKey(string path) => PilotPath.NormalizeInventoryPath(path)
        .Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

    private static bool Under(string parent, string path) =>
        string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    private async Task<PreviewSide> ReadIncludePreviewScopesAsync(RootDef root, IReadOnlyList<string> scopes,
        IReadOnlyList<string> owned, CancellationToken ct)
    {
        var entries = new List<PreviewEntry>();
        var warnings = new List<string>();
        var complete = true;
        // These reads share one memory budget; the independent other endpoint proceeds concurrently.
        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();
            if (entries.Count >= IncludePreviewEntryLimit)
            { complete = false; warnings.Add("Entry cap reached; remaining excluded subtrees were not read."); break; }
            if (owned.Any(path => Under(path, scope)))
            { complete = false; warnings.Add("Product-owned subtree remains protected and was not scanned."); continue; }
            var scan = await ReadIncludePreviewSideAsync(root, scope, IncludePreviewEntryLimit - entries.Count, ct)
                .ConfigureAwait(false);
            entries.AddRange(scan.Entries.Select(entry => entry with
                { Held = entry.Held || owned.Any(path => Under(path, entry.Path)) }));
            complete &= scan.Complete;
            if (scan.Warning is not null) warnings.Add(scan.Warning);
            // A capped collection has spent its traversal budget even if all entries were protected.
            if (!scan.Complete) break;
        }
        return new(entries, complete, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private async Task<PreviewSide> ReadIncludePreviewSideAsync(RootDef root, string prefix, int limit, CancellationToken ct)
    {
        try
        {
            if (IsLocal(root))
            {
                RequireSeparatedFromData(root);
                RequireLocalIdentity(root);
                var (volume, fileId) = LocalItems.ParseObjectId(root.RootObjectId);
                using var lease = PilotDirectoryLease.Open(root.RootPath, new PilotDirectoryIdentity(volume, fileId));
                // Check every ancestor, not just the selected leaf: a junction must never widen the scope.
                var components = prefix.Split('/');
                var ancestor = root.RootPath;
                foreach (var component in components.Take(components.Length - 1))
                {
                    ancestor = Path.Combine(ancestor, component);
                    if (!Directory.Exists(ancestor))
                        return new([], false, "An ancestor of the requested subtree is unavailable; absence is unknown.");
                    if (PilotPath.IsUnsafePath(ancestor))
                        return new([], false, "A linked or online-only ancestor was not followed. Choose a directly accessible subtree.");
                }
                var scanner = new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true };
                var scan = await scanner.ScanAsync(lease,
                    exactProductOwnedPaths: _paths.ProductOwnedPaths(_paths.RecoveryRoot)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    cancellationToken: ct, refreshPaths: [prefix], maxEntries: limit)
                    .ConfigureAwait(false);
                var complete = scan.Complete && scan.Decisions.Count == 0;
                return new(scan.Entries.Values.Select(entry => new PreviewEntry(entry.RelativePath,
                    entry.Kind, entry.Size, entry.ContentHash, entry.Link || entry.ContentUnreadable ||
                    entry.CloudAvailability == SyncCloudFileAvailability.OnlineOnly,
                    new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteUtc, DateTimeKind.Utc)))).ToArray(), complete,
                    !scan.Complete ? "The local preview reached its entry cap; choose a smaller subtree for complete counts."
                    : scan.Decisions.Count > 0 ? "Some local entries could not be inspected safely; absence is unknown."
                    : null);
            }
            if (string.Equals(root.Provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase))
            {
                var client = await _dropbox.OpenAsync(root, ct).ConfigureAwait(false);
                client.ReadTransientBudget = _settings.DropboxReadTransientBudget;
                var scan = await client.ListPreviewSubtreeAsync(prefix, limit, ct)
                    .ConfigureAwait(false);
                return new(scan.Entries.Select(entry => new PreviewEntry(entry.RelativePath,
                    entry.Kind, entry.Size, entry.ContentHash, entry.TransferBlockedReason is not null,
                    entry.ClientModifiedUtc ?? entry.ModifiedUtc)).ToArray(),
                    scan.Complete, scan.Complete ? null :
                    "The Dropbox preview is capped or incomplete; choose a smaller subtree for complete counts.");
            }
            return new([], false, "This provider has no read-only subtree preview. Its contents remain unknown.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or SyncRunRefusedException or System.ComponentModel.Win32Exception or HttpRequestException)
        {
            ct.ThrowIfCancellationRequested();
            return new([], false, "The subtree could not be read: " + SyncActivityText.Failure(error) +
                " Retry when accessible; no absence or deletion is inferred.");
        }
    }

    internal static SyncCliIncludePreviewData SummarizeIncludePreview(string pairId, string prefix,
        PreviewSide a, PreviewSide b, IReadOnlyList<SyncIgnoreRule>? ignores = null)
    {
        var warnings = new List<string>();
        if (a.Warning is not null) warnings.Add("A: " + a.Warning);
        if (b.Warning is not null) warnings.Add("B: " + b.Warning);
        var complete = a.Complete && b.Complete;
        if (!complete) warnings.Add("Incomplete inventory: observed counts are lower bounds; no one-sided absence claims are made.");
        warnings.Add("Metadata-only preview: equal sizes do not prove identical bytes. Content checks occur during ordinary sync.");
        var examples = new List<SyncCliIncludePreviewExample>();
        var heldPaths = new HashSet<string>(StringComparer.Ordinal);
        var heldFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in a.Entries.Concat(b.Entries))
        {
            if (!entry.Held && !LocalItems.IsArtifactPath(entry.Path) &&
                !SyncIgnorePolicy.IsIgnored(entry.Path, entry.Kind == PilotEntryKind.Directory, ignores ?? [])) continue;
            var key = PreviewKey(entry.Path);
            heldPaths.Add(key);
            if (entry.Kind == PilotEntryKind.Directory) heldFolders.Add(key);
        }
        Dictionary<string, PreviewEntry> Index(IReadOnlyList<PreviewEntry> entries)
        {
            var index = new Dictionary<string, PreviewEntry>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var key = PreviewKey(entry.Path);
                if (index.TryAdd(key, entry)) continue;
                heldPaths.Add(key);
                // Kind is uncertain in a collision: protect descendants too.
                heldFolders.Add(key);
            }
            return index;
        }
        var aByPath = Index(a.Entries);
        var bByPath = Index(b.Entries);
        bool Protected(string key)
        {
            if (heldPaths.Contains(key)) return true;
            var ancestor = key;
            while (ancestor.LastIndexOf('/') is var slash && slash >= 0)
            {
                ancestor = ancestor[..slash];
                if (heldFolders.Contains(ancestor)) return true;
            }
            return false;
        }
        var observedAt = DateTimeOffset.UtcNow;
        bool KnownTime(DateTimeOffset? value) => value is { } time &&
            ModificationTime.IsPlausible(time, observedAt);
        int copyA = 0, copyB = 0, newest = 0, ties = 0, folders = 0, identical = 0, held = 0;
        foreach (var key in aByPath.Keys.Union(bByPath.Keys, StringComparer.Ordinal))
        {
            aByPath.TryGetValue(key, out var left);
            bByPath.TryGetValue(key, out var right);
            var path = (left ?? right)!.Path;
            string action, reason;
            if (Protected(key) || !complete && (left is null || right is null))
            { held++; action = "held-unknown"; reason = "Unreadable, protected or incompletely inventoried; no absence inferred."; }
            else if (left is null)
            { copyB++; action = "copy-b-to-a"; reason = "One-sided item under ordinary no-baseline rules; never delete."; }
            else if (right is null)
            { copyA++; action = "copy-a-to-b"; reason = "One-sided item under ordinary no-baseline rules; never delete."; }
            else if (left.Kind == PilotEntryKind.Directory && right.Kind == PilotEntryKind.Directory)
            { folders++; action = "matching-folders"; reason = "Both folders exist; descendants are considered separately."; }
            else if (left.Kind != right.Kind)
            { held++; action = "held-unknown"; reason = "File/folder collision needs the ordinary planner's safe resolution."; }
            else if (Content.SameContent(left.Size, left.Hash, right.Size, right.Hash) == true)
            { identical++; action = "identical"; reason = "Matching size and content identity; no copy needed."; }
            else if (Content.SameContent(left.Size, left.Hash, right.Size, right.Hash) == false)
            {
                if (!KnownTime(left.ModifiedUtc) || !KnownTime(right.ModifiedUtc))
                { held++; action = "held-unknown"; reason = "Different files, but a modification time is missing, a sentinel or implausibly in the future; no winner is guessed."; }
                else if ((left.ModifiedUtc!.Value - right.ModifiedUtc!.Value).Duration() <= V2Settings.Default.TieWindow)
                { ties++; action = "held-tie"; reason = "Different files within the tie window require a user decision; no automatic tie-breaker."; }
                else
                {
                    newest++;
                    action = left.ModifiedUtc > right.ModifiedUtc ? "newest-a-to-b" : "newest-b-to-a";
                    reason = "Different files with known modification times: the newer version wins on reinclude.";
                }
            }
            else
            { held++; action = "content-check-needed"; reason = "Equal sizes without comparable hashes: content must be checked before declaring a difference or choosing a winner."; }
            if (examples.Count < IncludePreviewExampleLimit) examples.Add(new(path, action, reason));
        }
        return new(pairId, prefix, complete, a.Entries.Count, b.Entries.Count,
            new(copyA, copyB, 0, folders, identical, held, NewestWins: newest, HeldTies: ties), examples, warnings,
            ["Only excluded subtrees removed by this include request are inspected; already-included siblings are not scanned.",
             "Hypothetical reinclude preview, not an executable plan; existing baselines are not consulted.",
             $"One-sided items copy; identical files adopt; known differing files use newest-wins. Differences within {V2Settings.Default.TieWindow.TotalSeconds:g} seconds are held ties; unknown times are held, never guessed.",
             "No deletes, deletion evidence, baseline writes, policy writes, file hydration or endpoint writers.",
             "Include removes exclusions at or below the requested path. Ignore rules, protected folders and artifacts remain held; this inventory is not effect authority.",
             $"At most {IncludePreviewEntryLimit:N0} entries per side and {IncludePreviewExampleLimit} examples are retained."]);
    }
}
