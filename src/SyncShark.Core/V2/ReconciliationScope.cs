namespace Massdex.SyncShark.V2;

/// <summary>The pair-relative paths and subtrees whose inventory must be reconciled.</summary>
internal sealed class ReconciliationScope
{
    private readonly HashSet<string> _paths;
    private readonly HashSet<string> _subtrees;

    internal ReconciliationScope(IEnumerable<string> paths, IEnumerable<string> subtrees)
    {
        _paths = new(paths, StringComparer.OrdinalIgnoreCase);
        _subtrees = new(subtrees, StringComparer.OrdinalIgnoreCase);
    }

    internal static ReconciliationScope Root { get; } = new([], [string.Empty]);

    /// <summary>Whether a path belongs to the scope; an empty subtree denotes the root.</summary>
    internal bool Contains(string path) => _paths.Contains(path) || PairPath.CoveredBy(path, _subtrees);

    /// <summary>Whether a folder action could affect any scoped item or subtree.</summary>
    internal bool Touches(string path, bool folder) => Contains(path) || folder &&
        (path.Length == 0 && (_paths.Count != 0 || _subtrees.Count != 0) ||
         _paths.Any(item => PairPath.IsUnder(item, path)) ||
         _subtrees.Any(item => PairPath.IsUnder(item, path)));
}
