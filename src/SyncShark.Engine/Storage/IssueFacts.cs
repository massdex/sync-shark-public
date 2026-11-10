using System.Collections.Immutable;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>One pair's issue projection, maintained by StateStore under its connection lock.</summary>
internal sealed class IssueFacts(string pairId, long pairRow)
{
    public string PairId { get; } = pairId;
    public long PairRow { get; } = pairRow;
    private readonly Dictionary<long, (string[] Held, string[] Retained, string? Identity)> _keys = [];
    private readonly Dictionary<string, SortedDictionary<long, Issue>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<long, Issue>> _heldIdentities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<long>> _retained = new(StringComparer.Ordinal);
    private readonly Dictionary<long, IssueSubject> _retainedSubjects = [];
    public ImmutableDictionary<string, IssueSubject> Retained { get; private set; } =
        ImmutableDictionary.Create<string, IssueSubject>(StringComparer.Ordinal);

    // One replacement of one issue index entries: removal and insertion update the same dictionaries and derived Retained map.
    public void Replace(long id, Issue? issue)
    {
        if (_keys.Remove(id, out var previous))
        {
            foreach (var key in previous.Held) Unindex(_held, key, id);
            if (previous.Identity is { } heldIdentity) Unindex(_heldIdentities, heldIdentity, id);
            foreach (var key in previous.Retained)
            {
                var subjects = _retained[key];
                subjects.Remove(id);
                if (subjects.Count == 0)
                {
                    _retained.Remove(key);
                    Retained = Retained.Remove(key);
                }
                else Retained = Retained.SetItem(key, _retainedSubjects[subjects.Max]);
            }
            _retainedSubjects.Remove(id);
        }
        if (issue is null) return;
        var holds = IssuePolicy.Holds(issue.Kind, issue.Cause, issue.Subject);
        var held = holds ? IssuePolicy.HeldKeys(issue).ToArray() : [];
        var identity = holds ? IssuePolicy.HeldIdentity(issue.Kind, issue.Subject) : null;
        var retained = new Dictionary<string, IssueSubject>(StringComparer.Ordinal);
        IssuePolicy.AddFirstRunSubject(retained, issue);
        foreach (var key in held) Index(_held, key, id, issue);
        if (identity is not null) Index(_heldIdentities, identity, id, issue);
        if (retained.Count != 0) _retainedSubjects.Add(id, issue.Subject);
        foreach (var key in retained.Keys)
        {
            if (!_retained.TryGetValue(key, out var subjects)) _retained.Add(key, subjects = []);
            subjects.Add(id);
            Retained = Retained.SetItem(key, _retainedSubjects[subjects.Max]);
        }
        if (held.Length != 0 || retained.Count != 0 || identity is not null)
            _keys.Add(id, (held, retained.Keys.ToArray(), identity));
    }

    private static void Index(Dictionary<string, SortedDictionary<long, Issue>> map, string key, long id, Issue issue)
    {
        if (!map.TryGetValue(key, out var issues)) map.Add(key, issues = []);
        issues.Add(id, issue);
    }

    private static void Unindex(Dictionary<string, SortedDictionary<long, Issue>> map, string key, long id)
    {
        var issues = map[key];
        issues.Remove(id);
        if (issues.Count == 0) map.Remove(key);
    }

    public IssueKind? HeldKind(Row row, IReadOnlySet<long>? exceptIssues)
    {
        return (IssuePolicy.FirstCovered(row, (Facts: this, Except: exceptIssues),
            static (state, key) => state.Facts.Covering(key, state.Except)))?.Kind;
    }

    public bool HeldByOtherIssue(Row row, long selectedIssueId) =>
        IssuePolicy.FirstCovered(row, (Facts: this, Selected: selectedIssueId),
            static (state, key) => state.Facts.Covering(key, null, state.Selected)) is not null ||
        IdentityHolder(row, null, selectedIssueId) is not null;

    // After every path key: the observed Dropbox identity, looked up by its raw ID only while a move holds one.
    private Issue? IdentityHolder(Row row, IReadOnlySet<long>? exceptIssues, long? selectedIssueId) =>
        _heldIdentities.Count != 0 && row.Dropbox?.Id is { } id
            ? At(_heldIdentities, id, exceptIssues, selectedIssueId) : null;

    public Issue? Covering(string key, IReadOnlySet<long>? exceptIssues) => Covering(key, exceptIssues, null);

    private Issue? Covering(string key, IReadOnlySet<long>? exceptIssues, long? selectedIssueId)
    {
        if (_held.Count == 0) return null;
        if (At(_held, key, exceptIssues, selectedIssueId) is { } own) return own;
        foreach (var ancestor in PathNames.Ancestors(key))
            if (At(_held, ancestor, exceptIssues, selectedIssueId) is { } issue) return issue;
        return null;
    }

    /// <summary>Whether a hold covers the key or, for a whole subtree, sits under it. The descendant walk visits held
    /// keys only, so it grows with notices, never with the pair.</summary>
    public bool Overlaps(string key, bool includesDescendants) => Overlaps(key, includesDescendants, null);

    internal bool Overlaps(string key, bool includesDescendants, long? selectedIssue) =>
        Covering(key, null, selectedIssue) is not null || includesDescendants &&
        _held.Keys.Any(held => PathNames.IsUnderKey(held, key) && At(_held, held, null, selectedIssue) is not null);

    private static Issue? At(Dictionary<string, SortedDictionary<long, Issue>> map, string key,
        IReadOnlySet<long>? exceptIssues, long? selectedIssueId = null)
    {
        if (map.TryGetValue(key, out var holds))
            foreach (var issue in holds.Values)
                if (!IssuePolicy.Exempt(issue.Kind, issue.Subject, issue.Id, exceptIssues, selectedIssueId)) return issue;
        return null;
    }

    public Issue? HoldingIssue(Row row)
    {
        Issue? first = null;
        var keys = IssuePolicy.HoldKeys(row);
        Visit(keys.Key);
        Visit(keys.Local);
        Visit(keys.Dropbox);
        Visit(keys.ObservedLocal);
        Visit(keys.ObservedDropbox);
        Visit(keys.DestinationLocal);
        Visit(keys.DestinationDropbox);
        Consider(IdentityHolder(row, null, null));
        return first;

        void Visit(string? key)
        {
            if (key is null) return;
            Consider(At(_held, key, null));
            foreach (var ancestor in PathNames.Ancestors(key)) Consider(At(_held, ancestor, null));
        }

        void Consider(Issue? issue)
        {
            if (issue is not null && (first is null || issue.Id < first.Id)) first = issue;
        }
    }
}
