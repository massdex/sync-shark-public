using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>B-205: the in-use refusal names its arm (ordinary hold, later episode, or subject mismatch) without a
/// second read, and an age hit never parses the stored subject.</summary>
public static partial class ChangeTableChecks
{
    private enum HoldArmKind { Ordinary, Age, Mismatch, Matched, Empty }

    private static void HoldArmDiagnosticsCases()
    {
        foreach (var kind in Enum.GetValues<HoldArmKind>())
            WithStore((_, database) => HoldArmDiagnostics(database, kind));
    }

    private static void HoldArmDiagnostics(string database, HoldArmKind kind)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var footprint = new[] { new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false) };
        var page = 0UL;

        long Arrive(string? identity)
        {
            page++;
            var notices = new List<(string, ChangeNotice)>();
            if (identity is not null) notices.Add((PairId, Notice(Side.Local, File, identity)));
            return store.AcquireJournalPage(volume, page == 1 ? null : new JournalPosition(0, page - 1),
                new JournalPosition(0, page), [(PairId, generation, local.Epoch, local.Revision, false)], notices)!.Value;
        }

        WorkSelection Begin()
        {
            var selection = store.SelectWork(PairId, generation,
                new PathScope(store.Pair(PairId)!, store.Pairs())) ?? throw new InvalidOperationException(ExpectedFailure);
            Check.Require(store.BeginSelection(selection) is not null, "B-205 fixture selection must be admitted.");
            return selection;
        }

        LiveAdmissionStaleException? Admit(WorkSelection selection, long noticed)
        {
            try
            {
                store.ValidateLiveCoverage(selection, store.CaptureLiveCoverage(selection,
                    new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, (long)page), noticed),
                    new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, noticed)), footprint);
                return null;
            }
            catch (LiveAdmissionStaleException error) { return error; }
        }

        ItemRetryCandidate Episode() => store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity)], null), TextKey.FailLocalInUse, null,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);

        switch (kind)
        {
            case HoldArmKind.Ordinary:
            {
                Arrive(null);
                Episode();
                // Malformed on purpose: a read would throw, so the tagged ordinary arm proves zero episode reads.
                Check.Require(CorruptMemberSubject(database, "{\"broken\":") == 1,
                    "B-205 fixture: exactly one episode member subject is corrupted");
                store.EndWorkPass(PairId);
                var noticed = Arrive(Identity);
                var selection = Begin();
                store.Commit(PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, File,
                        new IssueSubject([new IssueItem(Side.Local, File, Identity)], null))]
                });
                var before = store.Issues(PairId).Count;
                var refused = Admit(selection, noticed);
                Check.Require(refused is not null &&
                    refused.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                    refused.HoldDiagnostic?.StartsWith("arm=ordinary-hold anchorWork=", StringComparison.Ordinal) == true &&
                    refused.HoldDiagnostic.Contains("throughWork=", StringComparison.Ordinal) &&
                    refused.WaitKey is null && refused.Detail?.EndsWith("(Local) is held.", StringComparison.Ordinal) == true &&
                    store.Issues(PairId).Count == before,
                    "B-205: an ordinary hold is tagged, keeps Reason and the ordinary null WaitKey, and never reads " +
                    "the episode: " + refused?.HoldDiagnostic);
                break;
            }
            case HoldArmKind.Age:
            {
                var noticed = Arrive(Identity);
                var selection = Begin();
                var episode = Episode();
                Check.Require(CorruptMemberSubject(database, "{\"broken\":") == 1,
                    "B-205 fixture: exactly one episode member subject is corrupted");
                var refused = Admit(selection, noticed);
                Check.Require(refused is not null &&
                    refused.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                    refused.HoldDiagnostic?.StartsWith("arm=episode-newer anchorWork=", StringComparison.Ordinal) == true &&
                    refused.HoldDiagnostic.Contains("episodeWork=" + episode.Token.WorkId, StringComparison.Ordinal) &&
                    refused.HoldDiagnostic.Contains("issueId=" + episode.Token.IssueId, StringComparison.Ordinal) &&
                    refused.HoldDiagnostic.Contains("subjectMatch=not-evaluated", StringComparison.Ordinal) &&
                    refused.WaitKey == PathNames.Key(File),
                    "B-205: a later episode is tagged by age and never parses the stored subject: " +
                    refused?.HoldDiagnostic);
                break;
            }
            case HoldArmKind.Mismatch:
            {
                Arrive(null);
                var episode = Episode();
                store.EndWorkPass(PairId);
                var noticed = Arrive(OtherIdentity);
                var selection = Begin();
                var before = store.Issues(PairId).Count;
                var refused = Admit(selection, noticed);
                Check.Require(refused is not null &&
                    refused.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                    refused.HoldDiagnostic?.StartsWith("arm=subject-mismatch anchorWork=", StringComparison.Ordinal) == true &&
                    refused.HoldDiagnostic.Contains("episodeWork=" + episode.Token.WorkId, StringComparison.Ordinal) &&
                    refused.HoldDiagnostic.Contains("issueId=" + episode.Token.IssueId, StringComparison.Ordinal) &&
                    refused.HoldDiagnostic.Contains("subjectMatch=false", StringComparison.Ordinal) &&
                    refused.WaitKey == PathNames.Key(File) && store.Issues(PairId).Count == before,
                    "B-205: a nonempty identified subject with no exact match is tagged as a mismatch: " +
                    refused?.HoldDiagnostic);
                break;
            }
            case HoldArmKind.Matched:
            {
                Arrive(null);
                Episode();
                store.EndWorkPass(PairId);
                var noticed = Arrive(Identity);
                var selection = Begin();
                Check.Require(Admit(selection, noticed) is null,
                    "B-205: an episode whose subject matches the selection admits unchanged");
                break;
            }
            case HoldArmKind.Empty:
            {
                Arrive(null);
                Episode();
                Check.Require(CorruptMemberSubject(database, StateEncoding.Envelope(new IssueSubject([], null))) == 1,
                    "B-205 fixture: exactly one episode member subject is emptied");
                store.EndWorkPass(PairId);
                var noticed = Arrive(OtherIdentity);
                var selection = Begin();
                Check.Require(Admit(selection, noticed) is null,
                    "B-205: an empty episode subject admits unchanged");
                break;
            }
        }
    }

    // Fixture-only: the path's episode member identities in the production statement's own order.
    private static List<object?> EpisodeMembers(string database)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var pairRow = connection.CreateCommand();
        pairRow.CommandText = "SELECT id FROM pair WHERE pair_id=$pair;";
        pairRow.Parameters.AddWithValue("$pair", PairId);
        var pair = (long)pairRow.ExecuteScalar()!;
        using var members = connection.CreateCommand();
        members.CommandText = StateSchema.ItemEpisodesAtPath;
        members.Parameters.AddWithValue(StateSchema.ParamPairRow, pair);
        members.Parameters.AddWithValue(StateSchema.ParamSide, (int)Side.Local);
        members.Parameters.AddWithValue(StateSchema.ParamPath, PathNames.Key(File));
        using var reader = members.ExecuteReader();
        var rows = new List<object?>();
        while (reader.Read())
            rows.Add(reader.IsDBNull(StateSchema.EpisodePathIdentity) ? null
                : reader.GetValue(StateSchema.EpisodePathIdentity));
        return rows;
    }

    // Fixture-only: set one member's stored subject by identity; the CHECK is bypassed so a decode would fail.
    private static int SetMemberSubject(string database, object? identity, string subject)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA ignore_check_constraints=ON;";
        pragma.ExecuteNonQuery();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE issue_member SET subject_json=$subject WHERE pair_id=" +
            "(SELECT id FROM pair WHERE pair_id=$pair) AND side=$side AND path_key=$path AND work_id IS NOT NULL " +
            "AND identity=$identity;";
        update.Parameters.AddWithValue("$subject", subject);
        update.Parameters.AddWithValue("$pair", PairId);
        update.Parameters.AddWithValue("$side", (int)Side.Local);
        update.Parameters.AddWithValue("$path", PathNames.Key(File));
        update.Parameters.AddWithValue("$identity", identity ?? DBNull.Value);
        return update.ExecuteNonQuery();
    }

    // Fixture-only corruption of the member subject: the CHECK is bypassed so a read of this row would fail.
    private static int CorruptMemberSubject(string database, string subject)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA ignore_check_constraints=ON;";
        pragma.ExecuteNonQuery();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE issue_member SET subject_json=$subject WHERE pair_id=" +
            "(SELECT id FROM pair WHERE pair_id=$pair) AND side=$side AND path_key=$path AND work_id IS NOT NULL;";
        update.Parameters.AddWithValue("$subject", subject);
        update.Parameters.AddWithValue("$pair", PairId);
        update.Parameters.AddWithValue("$side", (int)Side.Local);
        update.Parameters.AddWithValue("$path", PathNames.Key(File));
        return update.ExecuteNonQuery();
    }
}
