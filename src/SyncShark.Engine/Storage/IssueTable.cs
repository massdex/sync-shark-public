using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The issue list and its lifecycle (section 2.4).</summary>
public static class IssueTable
{
    // Repair's order: Local entries precede Dropbox entries.
    private static readonly Side[] ListingSides = [Side.Local, Side.Dropbox];
    private static readonly (string Name, object? Value)[] PublicCategoryValues =
    [
        (StateSchema.ParamPublicSourceKind, (int)IssuePolicy.FailureInfoSource),
        (StateSchema.ParamPublicTargetKind, (int)IssuePolicy.FailureInfoTarget),
        (StateSchema.ParamPublicCauses,
            JsonSerializer.Serialize(IssuePolicy.FailureInfoCauses.Select(cause => cause.ToString())))
    ];

    internal static ReadOnlySpan<(string Name, object? Value)> PublicCategoryParameters => PublicCategoryValues;

    // One raise: continue, refresh, replace and insert are one decision over the stored row, followed by at most one write.
    /// <summary>Continues an identical issue, refreshing only a changed proof or first-run marker; otherwise inserts
    /// or reopens it in the supplied transaction, unless its owner still retains the row. Collectors receive the exact
    /// row for post-commit delivery; IsNew is true after insert, replace or reopen, false after continue, refresh or retention.</summary>
    public static void Raise(SqliteConnection connection, SqliteTransaction transaction, string pairId, IssueKind kind,
        TextKey cause, string path, IssueSubject subject, DateTimeOffset now, ICollection<Issue>? raised = null,
        ICollection<(Issue Issue, bool IsNew)>? settled = null)
    {
        var incoming = cause;
        var existing = AtPath(connection, transaction, pairId, path, kind);
        if (existing is not null && kind == IssueKind.Failure && SharesLocalItem(existing.Subject, subject))
        {
            // Unfinished first-run work outlives the failure carrying it; a different local item inherits nothing.
            if (subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None ||
                existing.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
                subject = subject with { FirstRunDeletion = existing.Subject.FirstRunDeletion };
            // A dismissed late-recycled warning is closed history; an unrelated raise keeps its own cause.
            if ((IssuePolicy.ProtectsSubtree(cause.ToString()) || cause == TextKey.FailFirstSyncCheckWaiting) &&
                !IssuePolicy.ClosedLateRecycle(existing))
                cause = existing.Cause;
        }
        if (cause == TextKey.FailEarlierReplace && existing?.Cause is TextKey.RecoveryUnknown or TextKey.RecoveryRetained)
        {
            CollectRaised(existing, false, raised, settled);
            return;
        }
        if (Continues(existing, cause, subject))
        {
            if (Refreshes(existing, subject))
            {
                existing = existing with
                {
                    Subject = existing.Subject with
                    {
                        LocalHash = subject.LocalHash, FirstRunDeletion = subject.FirstRunDeletion
                    }
                };
                Execute(connection, transaction, StateSchema.UpdateIssueSubject,
                    (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, path), (StateSchema.ParamKind, (int)kind),
                    (StateSchema.ParamSubject, JsonSerializer.Serialize(existing.Subject)));
            }
            CollectRaised(existing, false, raised, settled);
            return;
        }
        var pair = kind == IssueKind.Failure ? PairFor(connection, transaction, pairId) : 0L;
        var invalid = kind == IssueKind.Failure &&
            StateStore.ReadInvalidFolderJob(connection, transaction, pair, path);
        var protectedIncoming = kind == IssueKind.Failure &&
            (cause == TextKey.RequiredRepairRetry || IssuePolicy.IsLocalAccessRetryCause(cause) ||
             subject.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None);
        if (invalid && !protectedIncoming)
        {
            var isNew = ReconcileInvalidFolderJob(connection, transaction, pairId, pair, path, invalid, existing);
            if (raised is not null || settled is not null)
                CollectRaised(AtPath(connection, transaction, pairId, path, kind)!, isNew, raised, settled);
            return;
        }
        // Reporting the failed attempt refreshes the same notice; nothing else re-causes a row its owner still holds.
        if (existing is not null &&
            (existing.Cause, cause) is not (TextKey.RequiredRepairRetry, TextKey.RequiredRepairRetry) &&
            existing.Cause != TextKey.FailDropboxDeleteJobInvalid &&
            (incoming != TextKey.FailRecycledLate && HoldsResponsibility(connection, transaction, pair, existing) ||
             IssuePolicy.UnknownMove(existing.Kind, existing.Subject)))
        {
            CollectRaised(existing, false, raised, settled);
            return;
        }
        if (existing is not null && kind == IssueKind.Failure)
            ReplaceExact(connection, transaction, pair, existing, cause, subject, now);
        else
            Execute(connection, transaction,
                existing is null ? StateSchema.InsertIssue : StateSchema.ReplaceIssue,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, path), (StateSchema.ParamKind, (int)kind),
                (StateSchema.ParamCause, cause.ToString()), (StateSchema.ParamSubject, JsonSerializer.Serialize(subject)),
                (StateSchema.ParamAt, now.UtcTicks));
        if (raised is not null || settled is not null)
            CollectRaised(AtPath(connection, transaction, pairId, path, kind)!, true, raised, settled);
    }

    private static void CollectRaised(Issue issue, bool isNew, ICollection<Issue>? raised,
        ICollection<(Issue Issue, bool IsNew)>? settled)
    {
        raised?.Add(issue);
        settled?.Add((issue, isNew));
    }

    /// <summary>Closes the specified kinds at a canonical path in the supplied transaction, with their member
    /// associations, leaving any row its owner still retains. A non-null scope spares an issue the result does not
    /// resolve (IssuePolicy.ScopedCloseAllowed).</summary>
    public static bool Close(SqliteConnection connection, SqliteTransaction transaction, string pairId, string path,
        IReadOnlyList<IssueKind> kinds, (SyncedEntry? Binding, bool Absent)? scope = null, Func<Issue, bool>? only = null)
    {
        var key = PathNames.Key(path);
        var closed = false;
        foreach (var kind in kinds)
        {
            var matches = new List<Issue>();
            using (var command = StorageCommand.Create(connection, transaction, StateSchema.SelectIssueByPathKeyKind,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, key), (StateSchema.ParamKind, (int)kind)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) matches.Add(ReadIssue(reader));
            foreach (var issue in matches)
                if ((scope is not { } scoped || IssuePolicy.ScopedCloseAllowed(issue, scoped.Binding, scoped.Absent)) &&
                    (only is null || only(issue)))
                    closed |= DeleteIssue(connection, transaction, pairId, issue);
        }
        return closed;
    }

    /// <summary>A pending note's own notice: a settlement closes it with the note; other Failures keep their owners.</summary>
    internal static bool IsRecoveryNotice(Issue issue) => issue.Cause is TextKey.RecoveryUnknown or TextKey.RecoveryRetained;

    /// <summary>Clears only the first-run marker of the Failure at the item's path when its subject holds exactly this
    /// item; any other row is unchanged. The Failure itself stays until a Close removes it.</summary>
    public static void AcknowledgeFirstRun(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        IssueItem item)
    {
        var issue = ReadOne(connection, transaction, StateSchema.SelectIssueByPathKind,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, item.Path),
            (StateSchema.ParamKind, (int)IssueKind.Failure));
        if (issue is null || !HasFirstRunWork(issue) || !issue.Subject.Items.Contains(item)) return;
        Execute(connection, transaction, StateSchema.UpdateIssueSubject,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, item.Path),
            (StateSchema.ParamKind, (int)IssueKind.Failure),
            (StateSchema.ParamSubject, JsonSerializer.Serialize(
                issue.Subject with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None })));
    }

    /// <summary>Deletes one issue with its member associations only while the stored row still equals expected.
    /// False means it changed, is gone or stays retained by its owner, and nothing was written.</summary>
    internal static bool CloseExact(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        Issue expected, bool acknowledgeFirstRun = false)
    {
        var stored = ReadOne(connection, transaction, StateSchema.SelectIssueById,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamId, expected.Id));
        if (stored is null || !stored.Matches(expected)) return false;
        if (acknowledgeFirstRun && HasFirstRunWork(stored))
        {
            foreach (var item in stored.Subject.Items.Where(item => item.Side == Side.Local))
                AcknowledgeFirstRun(connection, transaction, pairId, item);
            stored = ReadOne(connection, transaction, StateSchema.SelectIssueById,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamId, expected.Id));
            if (stored is null) return true;
        }
        return DeleteIssue(connection, transaction, pairId, stored);
    }

    internal static bool SetIgnored(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, Issue expected, bool ignored)
    {
        var stored = ReadOne(connection, transaction, StateSchema.SelectIssueById,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamId, expected.Id));
        if (stored is null || !stored.Matches(expected)) return false;
        return Execute(connection, transaction, StateSchema.SetIssueIgnored,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamId, expected.Id), (StateSchema.ParamIgnored, ignored)) != 0;
    }

    /// <summary>Reads the requested kinds, or all issues, in issue-id order, including dismissed rows.</summary>
    public static IReadOnlyList<Issue> List(SqliteConnection connection, string pairId,
        IReadOnlyList<IssueKind>? kinds = null)
    {
        var issues = new List<Issue>();
        using var command = StorageCommand.Create(connection, null,
            kinds is null ? StateSchema.SelectIssues : StateSchema.SelectIssuesByKinds,
            (StateSchema.ParamPair, pairId));
        if (kinds is not null)
            command.Parameters.AddWithValue(StateSchema.ParamKinds, JsonSerializer.Serialize(kinds));
        using var reader = command.ExecuteReader();
        while (reader.Read()) issues.Add(ReadIssue(reader));
        return issues;
    }

    /// <summary>Reads one issue only when its id belongs to the requested pair.</summary>
    public static Issue? ById(SqliteConnection connection, string pairId, long id) =>
        ReadOne(connection, null, StateSchema.SelectIssueById,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamId, id));

    /// <summary>One bounded page of non-dismissed issues across the pair, account or all pairs, ordered by issue id,
    /// and the filtered total. Both statements run in one deferred read transaction, disposed before return, so the
    /// page and count always see one snapshot. Dismissed rows are excluded in SQL and only the page is decoded; the
    /// path search calls the `PathNames` rule through its registered function. `between` is the one test reader's
    /// seam for proving the shared snapshot.</summary>
    public static (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenPage(SqliteConnection connection,
        string? pairId, string? account, string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset,
        Action? between = null, bool ignored = false)
    {
        connection.CreateFunction(StateSchema.PathContainsFunction,
            (string path, string text) => PathNames.Contains(path, text), isDeterministic: true);
        List<(string, Issue)> page = [];
        int total;
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            // A zero limit is a count-only request; it must not run the page statement as well.
            if (limit > 0)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = StateSchema.SelectIssuePage;
                BindPageFilter(command, pairId, account, search, kinds, limit, offset, ignored);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    page.Add((reader.GetString(StateSchema.IssueIgnored + 1), ReadIssue(reader)));
            }
            between?.Invoke();
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = StateSchema.CountIssuePage;
                BindPageFilter(count, pairId, account, search, kinds, null, null, ignored);
                total = checked((int)(long)count.ExecuteScalar()!);
            }
            transaction.Commit();
        }
        return (page, total);
    }

    /// <summary>Every non-dismissed issue of the requested kinds in issue-id order and the filtered total, read in
    /// one deferred read transaction and disposed before return. The whole keyset traversal and its one count see
    /// one snapshot, so a concurrent commit can neither hide a notice behind a reused issue id nor make the count
    /// disagree with the rows. `between` is the one test reader's seam for proving the shared snapshot.</summary>
    public static (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) PageSnapshot(
        SqliteConnection connection, IReadOnlyList<IssueKind>? kinds, int pageSize, Action? between = null, bool ignored = false)
    {
        connection.CreateFunction(StateSchema.PathContainsFunction,
            (string path, string text) => PathNames.Contains(path, text), isDeterministic: true);
        var rows = new List<(string, Issue)>();
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            // Ids strictly increase, so each page advances the key and a short page ends the traversal.
            for (var after = 0L; pageSize > 0;)
            {
                var page = PageAfter(connection, transaction, after, kinds, pageSize, ignored);
                rows.AddRange(page);
                between?.Invoke();
                if (page.Count < pageSize) break;
                after = page[^1].Issue.Id;
            }
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = StateSchema.CountIssuePage;
            BindPageFilter(count, null, null, null, kinds, null, null, ignored);
            var total = checked((int)(long)count.ExecuteScalar()!);
            transaction.Commit();
            return (rows, total);
        }
    }

    private static IReadOnlyList<(string PairId, Issue Issue)> PageAfter(SqliteConnection connection,
        SqliteTransaction transaction, long after, IReadOnlyList<IssueKind>? kinds, int limit, bool ignored)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.SelectIssuePageAfter,
            (StateSchema.ParamAfter, after), (StateSchema.ParamIgnored, ignored),
            (StateSchema.ParamKinds, kinds is null ? DBNull.Value : JsonSerializer.Serialize(kinds)),
            (StateSchema.ParamLimit, limit));
        BindPublicCategory(command);
        var rows = new List<(string, Issue)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(StateSchema.IssueIgnored + 1), ReadIssue(reader)));
        return rows;
    }

    private static void BindPageFilter(SqliteCommand command, string? pairId, string? account, string? search,
        IReadOnlyList<IssueKind>? kinds, int? limit, int? offset, bool ignored)
    {
        BindPublicCategory(command);
        command.Parameters.AddWithValue(StateSchema.ParamIgnored, ignored);
        command.Parameters.AddWithValue(StateSchema.ParamPair, (object?)pairId ?? DBNull.Value);
        command.Parameters.AddWithValue(StateSchema.ParamAccount, (object?)account ?? DBNull.Value);
        command.Parameters.AddWithValue(StateSchema.ParamKinds,
            kinds is null ? DBNull.Value : JsonSerializer.Serialize(kinds));
        command.Parameters.AddWithValue(StateSchema.ParamSearch,
            string.IsNullOrEmpty(search) ? DBNull.Value : search);
        if (limit is not null) command.Parameters.AddWithValue(StateSchema.ParamLimit, limit.Value);
        if (offset is not null) command.Parameters.AddWithValue(StateSchema.ParamOffset, offset.Value);
    }

    private static void BindPublicCategory(SqliteCommand command)
    {
        foreach (var (name, value) in PublicCategoryParameters)
            command.Parameters.AddWithValue(name, value!);
    }

    /// <summary>Maps held subject keys to the earliest holding issue's kind, including dismissed rows but excluding
    /// selected Failure retries. Subject-less Failures hold the issue path on both sides.</summary>
    public static IReadOnlyDictionary<string, IssueKind> HeldKinds(SqliteConnection connection, string pairId,
        IReadOnlySet<long>? retryIssues = null)
    {
        var kinds = new Dictionary<string, IssueKind>(StringComparer.Ordinal);
        using var command = StorageCommand.Create(connection, null, StateSchema.SelectHoldingSubjects,
            (StateSchema.ParamPair, pairId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = (IssueKind)reader.GetInt64(StateSchema.HoldingKind);
            // A selected Failure is skipped before its subject is decoded, so one malformed selected row cannot fail the read.
            if (IssuePolicy.Retried(kind, reader.GetInt64(StateSchema.HoldingId), retryIssues)) continue;
            var cause = StateEncoding.ReadEnum<TextKey>(reader, StateSchema.HoldingCause);
            var subject = StateEncoding.Decode<IssueSubject>(reader.GetString(StateSchema.HoldingSubject));
            if (!IssuePolicy.Holds(kind, cause, subject)) continue;
            IssuePolicy.AddHeldKeys(kind, reader.GetString(StateSchema.HoldingPath), subject, (kinds, kind),
                static (state, key) => state.kinds.TryAdd(key, state.kind));
        }
        return kinds;
    }

    /// <summary>Reconciles the complete skipped set atomically in the supplied transaction, leaving each side a live
    /// observation touched during this scan. One item has one notice: spellings of the same frozen key (a listing's
    /// spelling and a destructive walk's, for example) are one group, and a stored spelling keeps its notice.</summary>
    public static void ReplaceSkipped(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        long pair, byte[] scanId, IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> skipped, DateTimeOffset now)
    {
        var wanted = skipped.GroupBy(entry => PathNames.Key(entry.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var existing = new List<string>();
        using (var command = StorageCommand.Create(connection, transaction, StateSchema.SelectIssuePathsByKind,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamKind, (int)IssueKind.Skipped)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) existing.Add(reader.GetString(StateSchema.FirstColumn));
        var pathsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        // A gone path first frees its member keys, so a new spelling of the same key can claim them.
        foreach (var path in existing)
        {
            var key = PathNames.Key(path);
            if (pathsByKey.ContainsKey(key) || !wanted.ContainsKey(key))
                ReconcileSkip(connection, transaction, pairId, pair, scanId, path, [], now);
            else pathsByKey.Add(key, path);
        }
        foreach (var group in wanted)
            ReconcileSkip(connection, transaction, pairId, pair, scanId,
                pathsByKey.GetValueOrDefault(group.Key) ?? group.Value[0].Path, group.Value, now);
    }

    /// <summary>Applies one side's live skip observation to the path's one Skipped issue. A null skip is a positive
    /// clear of that side, not a failed read; the other side's member survives.</summary>
    public static void ObserveSkipped(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        long pair, Side side, string path, (TextKey Cause, IssueSubject Subject)? skip, DateTimeOffset now)
    {
        (TextKey Cause, IssueSubject Subject)? observed = null;
        if (skip is { } value)
            observed = SideOf([(path, value.Cause, value.Subject)], side) ??
                throw new ArgumentException(null, nameof(skip));
        MergeSkip(connection, transaction, pairId, pair, path,
            (member, _, current) => member == side ? observed : current, now);
    }

    /// <summary>Whether a protecting Local member sits at the key, at an ancestor of it or, for a folder, below it. The
    /// member's own cause decides: issue.cause can name the other side, and dismissal leaves members in place.</summary>
    internal static bool UnreadableTouches(SqliteConnection connection, string pairId, string key, bool isFolder) =>
        UnreadableProbe(connection, pairId, key, [key, .. PathNames.Ancestors(key)], isFolder);

    /// <summary>Whether a protecting Local member sits at the key or below it: two issue_member_skip seeks, the second
    /// stopping at its first row.</summary>
    internal static bool UnreadableAtOrUnder(SqliteConnection connection, string pairId, string key) =>
        UnreadableProbe(connection, pairId, key, [key], below: true);

    /// <summary>Whether a protecting Local member sits at one of the exact keys or, when asked, below the key; stops at
    /// the first hit and reads no more than the first descendant row.</summary>
    private static bool UnreadableProbe(SqliteConnection connection, string pairId, string key, string[] exact,
        bool below)
    {
        if (UnreadableAt(connection, pairId, exact)) return true;
        if (!below) return false;
        using var command = UnreadableBelow(connection, pairId, key);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    /// <summary>The protecting Local members at or below the key, for one involved folder.</summary>
    internal static IReadOnlyList<string> UnreadableUnder(SqliteConnection connection, string pairId, string key)
    {
        var keys = new List<string>();
        if (UnreadableAt(connection, pairId, [key])) keys.Add(key);
        using var command = UnreadableBelow(connection, pairId, key);
        using var reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(StateSchema.FirstColumn));
        return keys;
    }

    private static bool UnreadableAt(SqliteConnection connection, string pairId, string[] keys)
    {
        using var command = StorageCommand.Create(connection, null, StateSchema.UnreadableAtOrAbove,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamSide, (int)Side.Local),
            (StateSchema.ParamKeys, JsonSerializer.Serialize(keys)),
            (StateSchema.ParamCause, IssuePolicy.ProtectedSkipCause()));
        return command.ExecuteScalar() is not null;
    }

    private static SqliteCommand UnreadableBelow(SqliteConnection connection, string pairId, string key)
    {
        var (low, high) = PathNames.DescendantBounds(key);
        return StorageCommand.Create(connection, null, StateSchema.UnreadableBelow, (StateSchema.ParamPair, pairId),
            (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamLow, low), (StateSchema.ParamHigh, high),
            (StateSchema.ParamCause, IssuePolicy.ProtectedSkipCause()));
    }

    /// <summary>True while the path's Local member is itself protecting.</summary>
    internal static bool ProtectingMember(SqliteConnection connection, SqliteTransaction transaction, long pair,
        string key) => ReadSkipMember(connection, transaction, pair, Side.Local, key) is { } member &&
            IssuePolicy.ProtectsSubtree(member.Cause.ToString());

    /// <summary>Whether the Local member at the key protects and was recorded for exactly this item. A null stored
    /// identity never matches.</summary>
    internal static bool ReleasableMember(SqliteConnection connection, SqliteTransaction transaction, long pair,
        string key, string acquiredId)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.SkipMember,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamPath, key));
        using var reader = command.ExecuteReader();
        return reader.Read() && IssuePolicy.ProtectsSubtree(reader.GetString(StateSchema.SkipMemberSkipCause)) &&
            Equals(reader.GetValue(StateSchema.SkipMemberIdentity), StateEncoding.IdFor(Side.Local, acquiredId));
    }

    /// <summary>Removes only the Local member of the key's issue; the Dropbox member and the issue's other facts stay.</summary>
    internal static void RemoveLocalSkip(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        long pair, string key, DateTimeOffset now) => MergeSkip(connection, transaction, pairId, pair, key,
            (member, _, current) => member == Side.Local ? null : current, now);

    /// <summary>The upgrade's one write of the members a Skipped issue from before members never had, so the range
    /// answer can see it. Issue identity, dismissal and subject are untouched; a key two old spellings share keeps
    /// the first, as the unique index requires.</summary>
    internal static void BackfillSkipMembers(SqliteConnection connection, SqliteTransaction transaction)
    {
        var issues = new List<(long Issue, long Pair, string Path, TextKey Cause, IssueSubject Subject)>();
        using (var command = StorageCommand.Create(connection, transaction, StateSchema.MemberlessSkips,
            (StateSchema.ParamKind, (int)IssueKind.Skipped)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                issues.Add((reader.GetInt64(StateSchema.MemberlessIssue), reader.GetInt64(StateSchema.MemberlessPair),
                    reader.GetString(StateSchema.MemberlessPath),
                    StateEncoding.ReadEnum<TextKey>(reader, StateSchema.MemberlessCause),
                    StateEncoding.Decode<IssueSubject>(reader.GetString(StateSchema.MemberlessSubject))));
        var written = new HashSet<(long Pair, Side Side, string Key)>();
        foreach (var issue in issues)
            foreach (var side in ListingSides)
            {
                var key = PathNames.KeyFor(side, issue.Path);
                if (SideOf([(issue.Path, issue.Cause, issue.Subject)], side) is { } skip &&
                    written.Add((issue.Pair, side, key)))
                    InsertSkipMember(connection, transaction, issue.Issue, issue.Pair, side, key, skip.Cause, skip.Subject);
            }
    }

    /// <summary>Deletes Info issues and marks other kinds dismissed within the requested pair.</summary>
    public static void Dismiss(SqliteConnection connection, string pairId, long id) =>
        Execute(connection, null, StateSchema.DismissIssue,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamId, id), (StateSchema.ParamKind, (int)IssueKind.Info));

    /// <summary>Checks stored-kind authority without public projection or subject decoding; ignored rows still count.</summary>
    public static bool HasOpenIssue(SqliteConnection connection, string pairId, IssueKind kind)
    {
        using var command = StorageCommand.Create(connection, null, StateSchema.HasOpenIssue,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamKind, (int)kind));
        return (long)command.ExecuteScalar()! != 0;
    }

    /// <summary>Counts non-dismissed issues without decoding subjects, omitting zero-count kinds.</summary>
    public static IReadOnlyDictionary<IssueKind, int> Counts(SqliteConnection connection, string pairId, bool? ignored = null)
    {
        var counts = new Dictionary<IssueKind, int>();
        using var command = StorageCommand.Create(connection, null, StateSchema.CountIssues, (StateSchema.ParamPair, pairId),
            (StateSchema.ParamIgnored, ignored));
        BindPublicCategory(command);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            counts.Add((IssueKind)reader.GetInt64(StateSchema.IssueCountKind),
                checked((int)reader.GetInt64(StateSchema.IssueCountValue)));
        return counts;
    }

    /// <summary>Loads exact closure membership without decoding subjects; StateStore owns the requested kinds.</summary>
    internal static HashSet<(string Path, IssueKind Kind)> ClosureKeys(SqliteConnection connection, string pairId,
        IReadOnlyList<IssueKind> kinds)
    {
        var keys = new HashSet<(string Path, IssueKind Kind)>();
        using var command = StorageCommand.Create(connection, null, StateSchema.SelectClosureKeys,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamKinds, JsonSerializer.Serialize(kinds)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            keys.Add((PathNames.Key(reader.GetString(StateSchema.ClosurePath)), (IssueKind)reader.GetInt64(StateSchema.ClosureKind)));
        return keys;
    }

    /// <summary>The pair's root RequiredRepairRetry notice, or null when the root holds none. Callers pass the pair
    /// row they already resolved.</summary>
    internal static Issue? RetryNotice(SqliteConnection connection, SqliteTransaction? transaction, long pair) =>
        ReadOne(connection, transaction, StateSchema.SelectRootIssueByKind, (StateSchema.ParamPairRow, pair),
            (StateSchema.ParamKind, (int)IssueKind.Failure)) is
            { Cause: TextKey.RequiredRepairRetry } notice ? notice : null;

    /// <summary>Completion's close of the exact retry notice and its members; refused while source debt or durable
    /// retry responsibility remains. Returns the affected issue count.</summary>
    internal static int CloseRetryNotice(SqliteConnection connection, SqliteTransaction transaction, long pair,
        long issueId)
    {
        return CloseRetryProjection(connection, transaction, pair, issueId, StateSchema.CloseRetryNotice,
            RetryNoticeArguments(pair, issueId));
    }

    /// <summary>The generation transition's close of the old retry notice, compared against the captured fences and
    /// the stored episode verbatim. Returns the affected issue count.</summary>
    internal static int CloseSupersededRetryNotice(SqliteConnection connection, SqliteTransaction transaction,
        long pair, long issueId, long generation, long localEpoch, long localRevision, long dropboxEpoch,
        long dropboxRevision, V2.FailureRetryOwner.State expected)
    {
        return CloseRetryProjection(connection, transaction, pair, issueId, StateSchema.CloseSupersededRetryNotice,
        [
            .. RetryNoticeArguments(pair, issueId), (StateSchema.ParamGeneration, generation),
            (StateSchema.ParamLocalEpoch, localEpoch), (StateSchema.ParamLocalRevision, localRevision),
            (StateSchema.ParamDropboxEpoch, dropboxEpoch), (StateSchema.ParamDropboxRevision, dropboxRevision),
            (StateSchema.ParamExpectedAttempts, expected.Attempts),
            (StateSchema.ParamExpectedRetryAfter, expected.RetryAfter.UtcTicks)
        ]);
    }

    /// <summary>Ends a listing page wait: closes the exact notice only when no failed-Repair episode also owns it at
    /// this generation. A page-created notice (pageOnly) closes beside an item-only retry row. False leaves the
    /// notice for that episode. Source obligations are untouched.</summary>
    internal static bool ClosePageWaitNotice(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, long pair, long expectedGeneration, long issueId, bool pageOnly)
    {
        return CloseRetryProjection(connection, transaction, pair, issueId, StateSchema.ClosePageWaitNotice,
            [.. RetryNoticeArguments(pair, issueId), (StateSchema.ParamGeneration, expectedGeneration),
                (StateSchema.ParamPageOnly, pageOnly)]) != 0;
    }

    /// <summary>Removes every member association of the pair, ahead of its guarded deletion.</summary>
    internal static void DetachPairMembers(SqliteConnection connection, SqliteTransaction transaction, long pair) =>
        Execute(connection, transaction, StateSchema.DetachPairMembers, (StateSchema.ParamPairRow, pair));

    /// <summary>Starts the candidate's in-use lifecycle on its issue. Returns the affected issue count.</summary>
    internal static int StartItemEpisode(SqliteConnection connection, SqliteTransaction transaction, long pair,
        ItemRetryCandidate candidate, string? pendingJson) =>
        Execute(connection, transaction, StateSchema.StartItemEpisode, ItemFence(pair, candidate, pendingJson));

    /// <summary>The expected-state half of a claim: true while the lifecycle, work and fences still match. The attempt
    /// registry's reservation remains the claim.</summary>
    internal static bool TryClaimItemRetry(SqliteConnection connection, SqliteTransaction? transaction, long pair,
        ItemRetryCandidate expected, string? pendingJson)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.TryClaimItemRetry,
            ItemFence(pair, expected, pendingJson));
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Advances the lifecycle revision after a genuine in-use refusal, keeping the work. The revision is an
    /// expected-state fence, not an attempt count. Returns the affected issue count.</summary>
    internal static int CompleteFailedItemAttempt(SqliteConnection connection, SqliteTransaction transaction,
        long pair, ItemRetryCandidate expected, string? pendingJson) =>
        Execute(connection, transaction, StateSchema.CompleteFailedItemAttempt,
        [
            .. ItemFence(pair, expected, pendingJson),
            (StateSchema.ParamNextRevision, checked(expected.Token.Revision + 1))
        ]);

    /// <summary>Clears the exact lifecycle from its issue. Returns the affected issue count.</summary>
    internal static int ClearItemEpisode(SqliteConnection connection, SqliteTransaction transaction, long pair,
        ItemRetryToken token) =>
        Execute(connection, transaction, StateSchema.ClearItemEpisode, ItemLifecycle(pair, token));

    /// <summary>Changes the diagnosis within the in-use family, resetting the notice as a changed Raise does. Creates
    /// no attempt or opportunity. Returns the affected issue count.</summary>
    internal static int ReclassifyItemRetry(SqliteConnection connection, SqliteTransaction transaction, long pair,
        ItemRetryToken token, TextKey expectedCause, TextKey nextCause, DateTimeOffset now) =>
        Execute(connection, transaction, StateSchema.ReclassifyItemRetry,
        [
            .. ItemLifecycle(pair, token), (StateSchema.ParamExpectedCause, expectedCause.ToString()),
            (StateSchema.ParamNextCause, nextCause.ToString()), (StateSchema.ParamAt, now.UtcTicks)
        ]);

    /// <summary>The current in-use candidate of each requested issue, null when it has no lifecycle.</summary>
    internal static IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(SqliteConnection connection,
        SqliteTransaction? transaction, string pairId, long pair, IReadOnlyList<long> issueIds)
    {
        var candidates = new Dictionary<long, ItemRetryCandidate?>(issueIds.Count);
        foreach (var id in issueIds) candidates[id] = null;
        using var command = StorageCommand.Create(connection, transaction, StateSchema.ReadIssueRetryViews,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamIds, JsonSerializer.Serialize(issueIds)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (StateEncoding.ReadItemLifecycle(reader, StateSchema.RetryViewItemRetryEpisode) is not { } lifecycle)
                continue;
            var id = reader.GetInt64(StateSchema.RetryViewIssueId);
            candidates[id] = new ItemRetryCandidate(
                new ItemRetryToken(id, lifecycle.Episode, lifecycle.Revision, lifecycle.WorkId,
                    reader.GetInt64(StateSchema.RetryViewPairGeneration),
                    reader.GetInt64(StateSchema.RetryViewSourceEpoch),
                    StateEncoding.Decode<IssueSubject>(reader.GetString(StateSchema.RetryViewSubjectJson))),
                pairId, StateEncoding.ReadEnum<ItemRetryDispatch>(reader, StateSchema.RetryViewDispatchKind),
                reader.IsDBNull(StateSchema.RetryViewPendingOperationId)
                    ? null : reader.GetString(StateSchema.RetryViewPendingOperationId),
                reader.IsDBNull(StateSchema.RetryViewPendingGeneration)
                    ? null : reader.GetInt64(StateSchema.RetryViewPendingGeneration));
        }
        return candidates;
    }

    private static void ReconcileSkip(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        long pair, byte[] scanId, string path, IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> entries,
        DateTimeOffset now) => MergeSkip(connection, transaction, pairId, pair, path, (side, key, current) =>
        {
            using var touched = StorageCommand.Create(connection, transaction, StateSchema.SkipTouched,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamScan, scanId), (StateSchema.ParamSide, (int)side), (StateSchema.ParamPath, key));
            return (long)touched.ExecuteScalar()! != 0 ? current : SideOf(entries, side);
        }, now);

    // One skip merge: member removals, survivor projection and the issue raise land in one transaction and share the raised ids.
    // One issue per path: each side's own member holds its skip, and the issue projects the survivors.
    private static void MergeSkip(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        long pair, string path,
        Func<Side, string, (TextKey Cause, IssueSubject Subject)?, (TextKey Cause, IssueSubject Subject)?> next,
        DateTimeOffset now)
    {
        // One item owns one notice: look the issue up by its frozen key, so a different spelling observed by
        // another pass (a listing read and a destructive walk) updates the same row instead of adding one.
        var issue = ReadOne(connection, transaction, StateSchema.SelectIssueByPathKeyKind,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, PathNames.Key(path)),
            (StateSchema.ParamKind, (int)IssueKind.Skipped));
        var display = issue?.Path ?? path;
        var survivors = new List<(TextKey Cause, IssueSubject Subject)>(ListingSides.Length);
        var inserts = new List<(Side Side, string Key, TextKey Cause, IssueSubject Subject)>(ListingSides.Length);
        foreach (var side in ListingSides)
        {
            var key = PathNames.KeyFor(side, display);
            var member = ReadSkipMember(connection, transaction, pair, side, key);
            var wanted = next(side, key, member);
            var unchanged = member is { } stored && wanted is { } kept && stored.Cause == kept.Cause &&
                stored.Subject.Incoming == kept.Subject.Incoming && stored.Subject.HasSameItems(kept.Subject);
            if (member is not null && !unchanged)
                Execute(connection, transaction, StateSchema.RemoveSkipMember, (StateSchema.ParamPairRow, pair),
                    (StateSchema.ParamSide, (int)side), (StateSchema.ParamPath, key));
            if (wanted is not { } skip) continue;
            survivors.Add(skip);
            if (!unchanged) inserts.Add((side, key, skip.Cause, skip.Subject));
        }
        if (survivors.Count == 0)
        {
            if (issue is not null) DeleteIssue(connection, transaction, pairId, issue);
            return;
        }
        var raised = new List<Issue>(StateSchema.OneRow);
        Raise(connection, transaction, pairId, IssueKind.Skipped,
            survivors.Any(skip => skip.Cause == issue?.Cause) ? issue!.Cause : survivors[0].Cause, display,
            IssueSubject.FromDistinct(survivors.SelectMany(skip => skip.Subject.Items),
                survivors.Select(skip => skip.Subject.Incoming).FirstOrDefault(side => side is not null)),
            now, raised);
        foreach (var (side, key, cause, subject) in inserts)
            InsertSkipMember(connection, transaction, raised[0].Id, pair, side, key, cause, subject);
    }

    private static void InsertSkipMember(SqliteConnection connection, SqliteTransaction transaction, long issue,
        long pair, Side side, string key, TextKey cause, IssueSubject subject)
    {
        var id = subject.Items[0].Id;
        Execute(connection, transaction, StateSchema.InsertIssueMember,
            (StateSchema.ParamIssue, issue), (StateSchema.ParamPairRow, pair),
            (StateSchema.ParamWork, null), (StateSchema.ParamSide, (int)side), (StateSchema.ParamPath, key),
            (StateSchema.ParamIdentity, string.IsNullOrEmpty(id) ? null : StateEncoding.IdFor(side, id)),
            (StateSchema.ParamSubject, StateEncoding.Envelope(subject)),
            (StateSchema.ParamSkip, cause.ToString()));
    }

    private static (TextKey Cause, IssueSubject Subject)? ReadSkipMember(SqliteConnection connection,
        SqliteTransaction transaction, long pair, Side side, string key)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.SkipMember,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)side), (StateSchema.ParamPath, key));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (StateEncoding.ReadEnum<TextKey>(reader, StateSchema.SkipMemberSkipCause),
                StateEncoding.ReadEnvelope<IssueSubject>(reader, StateSchema.SkipMemberSubjectJson))
            : null;
    }

    // One side's slice of a path's skips: the first cause naming that side, and only that side's items.
    private static (TextKey Cause, IssueSubject Subject)? SideOf(
        IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> entries, Side side)
    {
        var naming = entries.Where(entry => entry.Subject.Items.Any(item => item.Side == side)).ToList();
        if (naming.Count == 0) return null;
        return (naming[0].Cause, IssueSubject.FromDistinct(
            naming.SelectMany(entry => entry.Subject.Items).Where(item => item.Side == side),
            naming.Select(entry => entry.Subject.Incoming).FirstOrDefault(incoming => incoming is not null)));
    }

    private static bool SharesLocalItem(IssueSubject stored, IssueSubject raised) =>
        raised.Items.Any(item => item.Side == Side.Local && stored.Items.Contains(item));

    private static bool HasFirstRunWork(Issue issue) => issue.Kind == IssueKind.Failure &&
        issue.Subject.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None;

    private static bool MayHoldResponsibility(Issue issue) => issue.Kind == IssueKind.Failure &&
        (issue.Cause is TextKey.RequiredRepairRetry or TextKey.FailDropboxDeleteJobInvalid or TextKey.FailRecycledLate ||
         IssuePolicy.IsLocalAccessRetryCause(issue.Cause));

    // Live retry debt or an in-use lifecycle. First-run work is not here: it may change cause, only not close.
    internal static bool HoldsResponsibility(SqliteConnection connection, SqliteTransaction? transaction, long pair,
        Issue issue)
    {
        if (!MayHoldResponsibility(issue)) return false;
        // A late-recycled warning has no automatic resolution; only the user ends it.
        if (issue.Cause == TextKey.FailRecycledLate) return !issue.Dismissed;
        if (issue.Cause == TextKey.FailDropboxDeleteJobInvalid)
            return StateStore.ReadInvalidFolderJob(connection, transaction, pair, issue.Path);
        if (issue.Cause == TextKey.RequiredRepairRetry)
        {
            using var protection = StorageCommand.Create(connection, transaction, StateSchema.RetryNoticeProtected,
                RetryNoticeArguments(pair, issue.Id));
            return (long)protection.ExecuteScalar()! != 0;
        }
        using var command = StorageCommand.Create(connection, transaction, StateSchema.ReadItemEpisode,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, issue.Id));
        using var reader = command.ExecuteReader();
        return reader.Read() &&
            StateEncoding.ReadItemLifecycle(reader, StateSchema.ItemEpisodeItemRetryEpisode) is not null;
    }

    // The path of the pair's unsettled pending note: that note still owns the path's one pending slot.
    internal static string? PendingPath(SqliteConnection connection, SqliteTransaction? transaction, long pair)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.PendingRead,
            StateSchema.ParamPairRow, pair);
        return StateEncoding.DecodePending(command.ExecuteScalar() as string) switch
        {
            CurrentPending current => current.Value.Path,
            LegacyPending legacy => legacy.Path,
            _ => null
        };
    }

    internal static bool AtPendingPath(Issue issue, string? pendingPath) => issue.Kind == IssueKind.Failure &&
        pendingPath is not null && PathNames.Same(issue.Path, pendingPath);

    private static long PairFor(SqliteConnection connection, SqliteTransaction transaction, string pairId)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.PairFence,
            StateSchema.ParamPair, pairId);
        return (long)command.ExecuteScalar()!;
    }

    private static bool DeleteIssue(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        Issue issue)
    {
        if (issue.Kind != IssueKind.Failure)
        {
            // Skip-only members cannot survive the foreign key's SET NULL action.
            DetachMembers(connection, transaction, issue.Id);
            return Execute(connection, transaction, StateSchema.DeleteIssueByPathKind,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, issue.Path),
                (StateSchema.ParamKind, (int)issue.Kind)) != 0;
        }
        var pair = PairFor(connection, transaction, pairId);
        var invalid = StateStore.ReadInvalidFolderJob(connection, transaction, pair, issue.Path);
        // A recovery notice outlives any row result while its note holds the slot (B-187); the note's settlement
        // closes it in the settling transaction.
        if (HasFirstRunWork(issue) || IsRecoveryNotice(issue) &&
            false || (issue.Cause == TextKey.FailDropboxDeleteJobInvalid
            ? invalid : HoldsResponsibility(connection, transaction, pair, issue))) return false;
        var members = CaptureMembers(connection, transaction, issue.Id);
        if (Execute(connection, transaction, StateSchema.CloseIssueExact,
            ExactArguments(connection, transaction, pair, issue)) == 0) return false;
        DeleteClosedMembers(connection, transaction, pair, members);
        if (invalid)
            ReconcileInvalidFolderJob(connection, transaction, pairId, pair, issue.Path, invalid, null);
        return true;
    }

    /// <summary>Projects all live invalid jobs at one exact root within the caller's contribution transaction.</summary>
    internal static void ReconcileInvalidFolderJob(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, long pair, string path) =>
        ReconcileInvalidFolderJob(connection, transaction, pairId, pair, path,
            StateStore.ReadInvalidFolderJob(connection, transaction, pair, path),
            ReadOne(connection, transaction, StateSchema.SelectIssueByPathKind,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, path),
                (StateSchema.ParamKind, (int)IssueKind.Failure)));

    private static bool ReconcileInvalidFolderJob(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, long pair, string path, bool invalid, Issue? issue)
    {
        if (issue is not null && (HasFirstRunWork(issue) ||
            (issue.Cause != TextKey.FailDropboxDeleteJobInvalid &&
             (HoldsResponsibility(connection, transaction, pair, issue) ||
              IssuePolicy.UnknownMove(issue.Kind, issue.Subject))))) return false;
        if (!invalid)
        {
            if (issue is not { Cause: TextKey.FailDropboxDeleteJobInvalid }) return false;
            var members = CaptureMembers(connection, transaction, issue.Id);
            if (Execute(connection, transaction, StateSchema.CloseIssueExact,
                ExactArguments(connection, transaction, pair, issue)) != 0)
                DeleteClosedMembers(connection, transaction, pair, members);
            return false;
        }
        if (issue is { Cause: TextKey.FailDropboxDeleteJobInvalid }) return false;
        var root = StateStore.ReadActiveFolderByRoot(connection, transaction, pair, path)
            ?? throw new InvalidDataException();
        // The projection owns its first-seen time: no caller supplies the closed notice's or a transaction's clock.
        var now = DateTimeOffset.UtcNow;
        var subject = new IssueSubject(
            [new IssueItem(root.DeletingSide, root.RootEntry.Path, root.RootEntry.Id)], null);
        if (issue is not null)
            ReplaceExact(connection, transaction, pair, issue, TextKey.FailDropboxDeleteJobInvalid, subject, now);
        else
            Execute(connection, transaction, StateSchema.InsertIssue,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, path),
                (StateSchema.ParamKind, (int)IssueKind.Failure),
                (StateSchema.ParamCause, TextKey.FailDropboxDeleteJobInvalid.ToString()),
                (StateSchema.ParamSubject, JsonSerializer.Serialize(subject)), (StateSchema.ParamAt, now.UtcTicks));
        return true;
    }

    private static int CloseRetryProjection(SqliteConnection connection, SqliteTransaction transaction, long pair,
        long issueId, string sql, (string Name, object? Value)[] arguments)
    {
        var members = CaptureMembers(connection, transaction, issueId);
        var closed = Execute(connection, transaction, sql, arguments);
        if (closed == 0) return closed;
        DeleteClosedMembers(connection, transaction, pair, members);
        var invalid = StateStore.ReadInvalidFolderJob(connection, transaction, pair, string.Empty);
        if (!invalid) return closed;
        using var command = StorageCommand.Create(connection, transaction, StateSchema.PairIdByRow,
            StateSchema.ParamPairRow, pair);
        var pairId = (string)command.ExecuteScalar()!;
        ReconcileInvalidFolderJob(connection, transaction, pairId, pair, string.Empty,
            invalid, null);
        return closed;
    }

    private static void ReplaceExact(SqliteConnection connection, SqliteTransaction transaction, long pair,
        Issue issue, TextKey cause, IssueSubject subject, DateTimeOffset now)
    {
        Execute(connection, transaction, StateSchema.ReplaceIssueExact,
            [.. ExactArguments(connection, transaction, pair, issue), (StateSchema.ParamCause, cause.ToString()),
             (StateSchema.ParamSubject, JsonSerializer.Serialize(subject)), (StateSchema.ParamAt, now.UtcTicks)]);
    }

    private static (string Name, object? Value)[] ExactArguments(SqliteConnection connection,
        SqliteTransaction transaction, long pair, Issue issue)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.ReadItemEpisode,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, issue.Id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException();
        var lifecycle = StateEncoding.ReadItemLifecycle(reader, StateSchema.ItemEpisodeItemRetryEpisode);
        return
        [
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, issue.Id),
            (StateSchema.ParamPath, issue.Path), (StateSchema.ParamKind, (int)issue.Kind),
            (StateSchema.ParamExpectedCause, issue.Cause.ToString()),
            (StateSchema.ParamExpected, reader.GetString(StateSchema.ItemEpisodeSubjectJson)),
            (StateSchema.ParamExpectedAt, issue.At.UtcTicks), (StateSchema.ParamExpectedDismissed, issue.Dismissed),
            (StateSchema.ParamEpisode, lifecycle?.Episode), (StateSchema.ParamRevision, lifecycle?.Revision),
            (StateSchema.ParamWork, lifecycle?.WorkId)
        ];
    }

    private static List<long> CaptureMembers(SqliteConnection connection, SqliteTransaction transaction, long issueId)
    {
        var members = new List<long>();
        using var command = StorageCommand.Create(connection, transaction, StateSchema.ReadIssueMembers,
            StateSchema.ParamIssue, issueId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            members.Add(reader.GetInt64(StateSchema.MemberId));
        }
        return members;
    }

    private static void DeleteClosedMembers(SqliteConnection connection, SqliteTransaction transaction, long pair,
        List<long> members)
    {
        if (members.Count != 0)
            Execute(connection, transaction, StateSchema.DeleteClosedIssueMembers,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamIds, JsonSerializer.Serialize(members)));
    }

    // A skipped member cannot outlive its issue, so every issue deletion goes through here first. No work is deleted.
    private static void DetachMembers(SqliteConnection connection, SqliteTransaction transaction, long issueId) =>
        Execute(connection, transaction, StateSchema.DetachIssueMembers, (StateSchema.ParamIssue, issueId));

    private static (string Name, object? Value)[] RetryNoticeArguments(long pair, long issueId) =>
    [
        (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, issueId),
        (StateSchema.ParamKind, (int)IssueKind.Failure),
        (StateSchema.ParamRetryCause, TextKey.RequiredRepairRetry.ToString())
    ];

    private static (string Name, object? Value)[] ItemLifecycle(long pair, ItemRetryToken token) =>
    [
        (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, token.IssueId),
        (StateSchema.ParamEpisode, token.EpisodeId), (StateSchema.ParamRevision, token.Revision),
        (StateSchema.ParamWork, token.WorkId), (StateSchema.ParamSubject, JsonSerializer.Serialize(token.Subject))
    ];

    private static (string Name, object? Value)[] ItemFence(long pair, ItemRetryCandidate candidate,
        string? pendingJson) =>
    [
        .. ItemLifecycle(pair, candidate.Token), (StateSchema.ParamGeneration, candidate.Token.PairGeneration),
        (StateSchema.ParamEpoch, candidate.Token.SourceEpoch),
        (StateSchema.ParamDispatch, candidate.Dispatch.ToString()),
        (StateSchema.ParamPendingOperation, candidate.PendingOperationId),
        (StateSchema.ParamPendingGeneration, candidate.PendingGeneration), (StateSchema.ParamPendingJson, pendingJson),
        (StateSchema.ParamFailureKind, (int)IssueKind.Failure)
    ];

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(connection, transaction, sql, arguments);
        return command.ExecuteNonQuery();
    }

    private static Issue? ReadOne(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(connection, transaction, sql, arguments);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadIssue(reader) : null;
    }

    internal static Issue ReadIssue(SqliteDataReader reader) => new(
        reader.GetInt64(StateSchema.IssueId), (IssueKind)reader.GetInt64(StateSchema.IssueKind),
        StateEncoding.ReadEnum<TextKey>(reader, StateSchema.IssueCause), reader.GetString(StateSchema.IssuePath),
        StateEncoding.Decode<IssueSubject>(reader.GetString(StateSchema.IssueSubject)),
        StateEncoding.ReadTime(reader, StateSchema.IssueAt),
        StateEncoding.ReadBool(reader, StateSchema.IssueDismissed),
        StateEncoding.ReadBool(reader, StateSchema.IssueIgnored));

    /// <summary>The stored issue of one kind at an exact path: the one lookup a raise decides over.</summary>
    internal static Issue? AtPath(SqliteConnection connection, SqliteTransaction? transaction, string pairId,
        string path, IssueKind kind) => ReadOne(connection, transaction, StateSchema.SelectIssueByPathKind,
        (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, path), (StateSchema.ParamKind, (int)kind));

    /// <summary>True when the stored issue continues a raise of this cause and items, instead of a new raise.
    /// A dismissed late-recycled warning never continues: every raise of that cause is a new incident.</summary>
    internal static bool Continues([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Issue? existing,
        TextKey cause, IssueSubject subject) =>
        existing is not null && existing.Cause == cause && existing.Subject.HasSameItems(subject) &&
        !(existing.Dismissed && cause == TextKey.FailRecycledLate);

    /// <summary>True when a continuing issue must still refresh its stored local proof or first-run marker.</summary>
    internal static bool Refreshes(Issue existing, IssueSubject subject) =>
        existing.Subject.LocalHash != subject.LocalHash ||
        existing.Subject.FirstRunDeletion != subject.FirstRunDeletion;
}
