using Massdex.SyncShark;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine;

/// <summary>What is in sync for one pair: ignore rules, exclusions and nested pairs (section 2.3, D12, D25).
/// Exclusions and nested roots are compared as local-spelling keys, whichever side they belong to.</summary>
public sealed class PathScope
{
    private readonly PairConfig pair;
    private readonly IReadOnlyList<PairConfig> allPairs;
    private readonly string? defaultIgnoreRules;
    private readonly IReadOnlyList<SyncIgnoreRule> ignoreRules;
    private readonly IReadOnlyList<SyncIgnoreRule> ignoredFolderKeepRules;
    private readonly PrefixSet excluded = new();
    private readonly string[] localNestedRoots;
    private readonly string[] dropboxNestedRoots;
    private readonly string[] localNestedRootKeys;
    private readonly string[] dropboxNestedRootKeys;

    /// <summary>Builds the scope of one pair; other enabled pairs nested inside it are held out of sync.
    /// <paramref name="defaultIgnoreRules"/> uses the pair rule syntax; null applies none.</summary>
    public PathScope(PairConfig pair, IReadOnlyList<PairConfig> allPairs, string? defaultIgnoreRules = null)
    {
        this.pair = pair;
        this.allPairs = allPairs;
        this.defaultIgnoreRules = defaultIgnoreRules;
        ignoreRules = SyncIgnorePolicy.Normalize(SyncIgnorePolicy.Parse(defaultIgnoreRules ?? "")
            .Concat(pair.IgnoreRules.SelectMany(SyncIgnorePolicy.Parse)).ToArray());
        ignoredFolderKeepRules = SyncIgnorePolicy.Normalize(
            (pair.IgnoredFolderKeepRules ?? []).SelectMany(SyncIgnorePolicy.Parse).ToArray());
        foreach (var exclusion in pair.Exclusions)
            excluded.Add(PathNames.KeyFor(exclusion.Side, exclusion.Path));

        var local = new List<string>();
        var dropbox = new List<string>();
        foreach (var other in allPairs)
        {
            if (!other.Enabled || other.PairId == pair.PairId) continue;
            if (PathNames.RelativeUnderLocalRoot(pair.LocalRoot, other.LocalRoot) is { } localRoot) local.Add(localRoot);
            if (other.ConnectionId == pair.ConnectionId &&
                PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot),
                    PathNames.TrimDropboxRoot(other.DropboxRoot)) is { } dropboxRoot) dropbox.Add(dropboxRoot);
        }
        localNestedRoots = local.ToArray();
        dropboxNestedRoots = dropbox.ToArray();
        // Bilateral (2.3): a nested root keeps the row out of sync whichever side's path the row has.
        localNestedRootKeys = CacheNestedRootKeys(localNestedRoots, Side.Local);
        dropboxNestedRootKeys = CacheNestedRootKeys(dropboxNestedRoots, Side.Dropbox);
    }

    /// <summary>True when either side's path is ignored or inside an exclusion or nested root.</summary>
    public bool Excludes(string? localPath, string? dropboxPath, bool isFolder) =>
        localPath is not null && ExcludesPath(localPath, isFolder, isDropbox: false) ||
        dropboxPath is not null && ExcludesPath(dropboxPath, isFolder, isDropbox: true);

    /// <summary>Tests both destinations of a move, deriving a missing side through the shared saved-parent mapping.</summary>
    public bool LeavesScope<T>(string? localPath, string? dropboxPath, bool isFolder, T state,
        Func<T, Side, string, SyncedEntry?> readSaved)
    {
        if (localPath is null && dropboxPath is not null)
            localPath = PathNames.MappedDestination(Side.Local, dropboxPath, state, readSaved);
        else if (dropboxPath is null && localPath is not null)
            dropboxPath = PathNames.MappedDestination(Side.Dropbox, localPath, state, readSaved);
        return Excludes(localPath, dropboxPath, isFolder);
    }

    /// <summary>True when every retained ignored entry is still ignored and matched by a saved keep selector;
    /// the caller supplies the actual-content walk, and an entry that is no longer ignored makes this false.</summary>
    public bool CoversIgnoredContents(IEnumerable<Entry> retained)
    {
        foreach (var entry in retained)
            if (!SyncIgnorePolicy.IsIgnored(entry.Path, entry.IsFolder, ignoreRules) ||
                !SyncIgnorePolicy.IsIgnored(entry.Path, entry.IsFolder, ignoredFolderKeepRules)) return false;
        return true;
    }

    /// <summary>True when a nested root of that side is at or under path.</summary>
    public bool HoldsNestedRoot(Side side, string path) => NestedRootTouch(side, path, savedKey: null);

    /// <summary>True when a nested root of that side is at or under path, or the path's own key or the caller's
    /// already computed join key for that side lies at or under one of them.</summary>
    public bool OverlapsNestedRoot(Side side, string path, string key) => NestedRootTouch(side, path, key);

    // One walk of the roots answers both questions; with no saved key only the direction holds asks for is tested.
    private bool NestedRootTouch(Side side, string path, string? savedKey)
    {
        var pathKey = PathNames.KeyFor(side, path);
        var roots = side == Side.Local ? localNestedRootKeys : dropboxNestedRootKeys;
        for (var i = 0; i < roots.Length; i++)
        {
            if (PathNames.IsSelfOrUnder(roots[i], pathKey)) return true;
            if (savedKey is not null && (PathNames.IsSelfOrUnderKey(savedKey, roots[i]) ||
                PathNames.IsSelfOrUnderKey(pathKey, roots[i]))) return true;
        }
        return false;
    }

    /// <summary>The nested roots of one side, relative to that side's pair root.</summary>
    public IReadOnlyList<string> NestedRoots(Side side) =>
        side == Side.Local ? localNestedRoots : dropboxNestedRoots;

    /// <summary>The same scope minus that one exclusion; used only by ExclusionRunner.</summary>
    public PathScope Without(Exclusion e) => new(
        pair with { Exclusions = pair.Exclusions.Where(exclusion => exclusion != e).ToArray() }, allPairs,
        defaultIgnoreRules);

    // A lost race builds an identical immutable value once; the scope's inputs never change after construction.
    private PathScope? configured;

    /// <summary>This pair's configured scope: the same pair with no exclusions, since an unapplied exclusion is not
    /// scope retirement (2.4). Derived once for this immutable scope's lifetime, and the scope itself when it holds
    /// no exclusions.</summary>
    public PathScope Configured
    {
        get
        {
            if (pair.Exclusions.Count == 0) return this;
            return configured ??= new(pair with { Exclusions = [] }, allPairs, defaultIgnoreRules);
        }
    }

    /// <summary>This pair's scope with no ignore rules (including the defaults) and that exclusion removed; used only
    /// by ExclusionRunner. The pair and pair list are this scope's, which match the run's while the instance lock is
    /// held.</summary>
    public PathScope ProtectionWithout(Exclusion exclusion) =>
        new(pair with
        {
            IgnoreRules = [],
            Exclusions = pair.Exclusions.Where(e => e != exclusion).ToArray()
        }, allPairs);

    private bool ExcludesPath(string path, bool isFolder, bool isDropbox) =>
        SyncIgnorePolicy.IsIgnored(path, isFolder, ignoreRules) ||
        excluded.Covers(isDropbox ? PathNames.LocalSpelling(path) : path);

    // The keys are computed once here, and the same instances form part of the exclusion set.
    private string[] CacheNestedRootKeys(string[] roots, Side side)
    {
        var keys = new string[roots.Length];
        for (var i = 0; i < roots.Length; i++) excluded.Add(keys[i] = PathNames.KeyFor(side, roots[i]));
        return keys;
    }
}
