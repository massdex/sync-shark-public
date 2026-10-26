namespace Massdex.SyncShark.V2;

/// <summary>Pure path membership and strict-container index for planning holds.</summary>
internal sealed class PendingPathIndex
{
    private const byte LinkKind = 1;
    private const byte StubKind = 2;
    private const byte PendingKind = 4;
    private readonly HashSet<string> _all;
    private readonly Dictionary<string, byte> _containers = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _hasLinks;
    private readonly bool _hasStubs;
    private readonly bool _hasPending;

    internal PendingPathIndex(IEnumerable<string> pending) : this([], [], pending) { }

    internal PendingPathIndex(IReadOnlyCollection<string> links, IReadOnlyCollection<string> stubs,
        IEnumerable<string> pending)
    {
        _all = new HashSet<string>(links.Concat(stubs), StringComparer.OrdinalIgnoreCase);
        _hasLinks = links.Count > 0;
        _hasStubs = stubs.Count > 0;
        AddContainers(links, LinkKind);
        AddContainers(stubs, StubKind);
        foreach (var path in pending)
        {
            _all.Add(path);
            _hasPending = true;
            AddContainer(path, PendingKind);
        }
    }

    private void AddContainers(IEnumerable<string> paths, byte kind)
    {
        foreach (var path in paths) AddContainer(path, kind);
    }

    private void AddContainer(string path, byte kind)
    {
        for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
            _containers[path[..cut]] = (byte)(_containers.GetValueOrDefault(path[..cut]) | kind);
    }

    // Unlike Covers/Contains, the settle overlap predicate does not treat empty as the root.
    internal bool Overlaps(string path) => _all.Contains(path) ||
        PairPath.Ancestors(path).Any(_all.Contains) || path.Length > 0 && _containers.ContainsKey(path);

    internal int Count => _all.Count;
    internal bool Covers(string path) => PairPath.CoveredBy(path, _all);
    internal bool Contains(string path) => _all.Count > 0 && (path.Length == 0 || _containers.ContainsKey(path));
    internal bool ContainsLink(string path) =>
        path.Length == 0 ? _hasLinks : (_containers.GetValueOrDefault(path) & LinkKind) != 0;
    internal bool ContainsStub(string path) =>
        path.Length == 0 ? _hasStubs : (_containers.GetValueOrDefault(path) & StubKind) != 0;
    internal bool ContainsPending(string path) =>
        path.Length == 0 ? _hasPending : (_containers.GetValueOrDefault(path) & PendingKind) != 0;
}
