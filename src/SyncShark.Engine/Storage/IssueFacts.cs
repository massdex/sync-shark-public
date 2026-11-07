using System.Collections.Immutable;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>One pair's issue projection, maintained by StateStore under its connection lock.</summary>
internal sealed class IssueFacts(string pairId, long pairRow)
{
    public string PairId { get; } = pairId;
    public long PairRow { get; } = pairRow;
    private readonly Dictionary<long, (string[] Held, string[] Retained)> _keys = [];
    private readonly Dictionary<string, SortedDictionary<long, Issue>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<long>> _retained = new(StringComparer.Ordinal);
    private readonly Dictionary<long, IssueSubject> _retainedSubjects = [];
    public ImmutableDictionary<string, IssueSubject> Retained { get; private set; } =
        ImmutableDictionary.Create<string, IssueSubject>(StringComparer.Ordinal);

    public void Replace(long id, Issue? issue)
    {
        if (_keys.Remove(id, out var previous))
        {
            foreach (var key in previous.Held)
            {
                var holds = _held[key];
                holds.Remove(id);
                if (holds.Count == 0) _held.Remove(key);
            }
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
        var held = IssuePolicy.Holds(issue.Kind, issue.Cause)
            ? IssuePolicy.HeldKeys(issue).ToArray() : [];
        var retained = new Dictionary<string, IssueSubject>(StringComparer.Ordinal);
        IssuePolicy.AddFirstRunSubject(retained, issue);
        foreach (var key in held)
        {
            if (!_held.TryGetValue(key, out var holds)) _held.Add(key, holds = []);
            holds.Add(id, issue);
        }
        if (retained.Count != 0) _retainedSubjects.Add(id, issue.Subject);
        foreach (var key in retained.Keys)
        {
            if (!_retained.TryGetValue(key, out var subjects)) _retained.Add(key, subjects = []);
            subjects.Add(id);
            Retained = Retained.SetItem(key, _retainedSubjects[subjects.Max]);
        }
        if (held.Length != 0 || retained.Count != 0) _keys.Add(id, (held, retained.Keys.ToArray()));
    }

    public IssueKind? HeldKind(Row row, IReadOnlySet<long>? exceptIssues)
    {
        return IssuePolicy.FirstCovered(row, (Facts: this, Except: exceptIssues),
            static (state, key) => state.Facts.Covering(key, state.Except))?.Kind;
    }

    public bool HeldByOtherIssue(Row row, long selectedIssueId) =>
        IssuePolicy.FirstCovered(row, (Facts: this, Selected: selectedIssueId),
            static (state, key) => state.Facts.Covering(key, null, state.Selected)) is not null;

    public Issue? Covering(string key, IReadOnlySet<long>? exceptIssues) => Covering(key, exceptIssues, null);

    private Issue? Covering(string key, IReadOnlySet<long>? exceptIssues, long? selectedIssueId)
    {
        if (_held.Count == 0) return null;
        if (At(key, exceptIssues, selectedIssueId) is { } own) return own;
        foreach (var ancestor in PathNames.Ancestors(key))
            if (At(ancestor, exceptIssues, selectedIssueId) is { } issue) return issue;
        return null;
    }

    /// <summary>Whether a hold covers the key or, for a whole subtree, sits under it. The descendant walk visits held
    /// keys only, so it grows with notices, never with the pair.</summary>
    public bool Overlaps(string key, bool includesDescendants) => Covering(key, null) is not null ||
        includesDescendants && _held.Keys.Any(held => PathNames.IsUnderKey(held, key));

    private Issue? At(string key, IReadOnlySet<long>? exceptIssues, long? selectedIssueId = null)
    {
        if (_held.TryGetValue(key, out var holds))
            foreach (var issue in holds.Values)
                if (issue.Id != selectedIssueId && !IssuePolicy.Retried(issue.Kind, issue.Id, exceptIssues)) return issue;
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
        return first;

        void Visit(string? key)
        {
            if (key is null) return;
            Consider(At(key, null));
            foreach (var ancestor in PathNames.Ancestors(key)) Consider(At(ancestor, null));
        }

        void Consider(Issue? issue)
        {
            if (issue is not null && (first is null || issue.Id < first.Id)) first = issue;
        }
    }
}
