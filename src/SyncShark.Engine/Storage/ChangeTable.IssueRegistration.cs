using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Issue registration facts: the member registration lookup and freshness rules AttachIssueWork applies, and
/// the read-only repeated name-clash answer built from them.</summary>
internal sealed partial class ChangeTable
{
    /// <summary>Covered when the open name clash at the path continues this subject and already registers the member
    /// with its current fences and evidence; Attach when it continues but the member is missing or stale; else Raise.
    /// Reads only the issue at the path and the member's own registration rows.</summary>
    internal (ClashRefusal State, Issue? Existing) ClashRefusalState(WorkSelection selection, string path,
        IssueSubject subject, (ChangeNotice Notice, bool IsFolder, SyncedEntry? Before) work)
    {
        using var transaction = _connection.BeginTransaction(deferred: true);
        var pair = RequireCurrent(transaction, selection);
        RequireAnchor(transaction, pair.Row, selection);
        var existing = IssueTable.AtPath(_connection, transaction, selection.PairId, path, IssueKind.NameClash);
        if (existing is null || existing.Dismissed ||
            !IssueTable.Continues(existing, TextKey.ClashNameTaken, subject) || IssueTable.Refreshes(existing, subject))
            return (ClashRefusal.Raise, null);
        if (work.Notice.Side != selection.Side) throw new ArgumentException(null, nameof(work));
        var covered = IssueRegistration(transaction, pair.Row, existing.Id, work.Notice) is { } stored &&
            SameFences(stored, selection) && RegisteredNotice(stored, work.Notice) == stored.Notice &&
            MemberIssue(transaction, stored.WorkId) == (existing.Id, StateEncoding.Envelope(existing.Subject));
        return (ClashRefusal.Covered, existing);
    }

    private WorkRow? IssueRegistration(SqliteTransaction transaction, long pair, long issue, ChangeNotice notice) =>
        Rows(transaction, StateSchema.Registration, (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, issue),
            (StateSchema.ParamSide, (int)notice.Side),
            (StateSchema.ParamPath, PathNames.KeyFor(notice.Side, notice.Path)),
            (StateSchema.ParamIdentity, Identity(notice.Side, notice.Id))).FirstOrDefault();

    private static bool SameFences(WorkRow stored, WorkSelection parent) =>
        stored.PairGeneration == parent.PairGeneration && stored.SourceEpoch == parent.SourceEpoch;

    // The notice a registration carries once this notice is attached: dirty evidence is merged, never erased.
    private static string RegisteredNotice(WorkRow stored, ChangeNotice notice)
    {
        var incoming = StateEncoding.Envelope(notice);
        if (stored.Notice == incoming) return incoming;
        var merged = MergeContent(StateEncoding.DecodeEnvelope<ChangeNotice>(stored.Notice!) ??
            throw new InvalidDataException(), notice);
        return ReferenceEquals(merged, notice) ? incoming : StateEncoding.Envelope(merged);
    }

    private (long? Issue, string Subject)? MemberIssue(SqliteTransaction transaction, long work) =>
        Read<(long? Issue, string Subject)?>(transaction, StateSchema.WorkIssue, static reader => (
                Number(reader, StateSchema.WorkIssueId), reader.GetString(StateSchema.WorkIssueSubjectJson)),
            (StateSchema.ParamWork, work)).FirstOrDefault();
}
