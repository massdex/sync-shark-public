using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    // Row 43a through the real store: a held file's wait is captured across a case-only respelling, and the respelling
    // commits under that wait's own attempt and fences while the episode, its notice and its first-run work stay for
    // the content attempt. Anything wider is refused before a write.
    private static void CaseRenameKeepsWait(string database)
    {
        const string Respelled = "Parent/FILE";
        const string DropboxIdentity = "dropbox-identity";
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)], []);
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File, false, Identity, 1, 0,
            DropboxIdentity, "hash");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        // The first-run fixture: the wait carries unfinished first-run work, which only its content attempt may end.
        var held = store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity), new IssueItem(Side.Dropbox, File, DropboxIdentity)],
                null) { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.Item },
            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        IssueSubject Fresh(params IssueItem[] items) => new(items, null);
        var respelled = Fresh(new IssueItem(Side.Local, Respelled, Identity), new IssueItem(Side.Dropbox, File, DropboxIdentity));
        Exception? Refusal(Action write)
        {
            try { write(); return null; }
            catch (Exception error) when (error is LiveAdmissionStaleException or ArgumentException) { return error; }
        }
        bool SameWait(ItemRetryCandidate? candidate) => candidate is not null &&
            candidate.Token.IssueId == held.Token.IssueId && candidate.Token.EpisodeId == held.Token.EpisodeId &&
            candidate.Token.Revision == held.Token.Revision && candidate.Token.WorkId == held.Token.WorkId;

        Check.Require(SameWait(store.ReadItemRetryForSubject(PairId, generation, Respelled, respelled)),
            "43a: the held wait is captured across a Local case-only respelling");
        Check.Require(Refusal(() => store.ReadItemRetryForSubject(PairId, generation, Respelled, respelled,
                registeredWithin: true)) is LiveAdmissionStaleException,
            "43a: the registered-subset capture stays exact");
        Check.Require(Refusal(() => store.ReadItemRetryForSubject(PairId, generation, Respelled,
                Fresh([.. respelled.Items, new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)]))) is LiveAdmissionStaleException,
            "43a: an extra endpoint is not the held wait");

        // Counted work, by sqlite3_trace as in Statements: only an exact miss runs the fallback's saved-binding read
        // (SelectSyncedByLocalKey); an exact capture and a path with no episode run none (43a).
        int FallbackReads(Action read)
        {
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var prefix = "SELECT " + StateSchema.SyncedColumns + " FROM synced_entry WHERE pair_id = ";
            var reads = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                if (sql.utf8_to_string().StartsWith(prefix, StringComparison.Ordinal)) reads++;
            }), null);
            try { read(); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            return reads;
        }
        ItemRetryCandidate? exact = null;
        Check.Require(FallbackReads(() => exact = store.ReadItemRetryForSubject(PairId, generation, File,
                held.Token.Subject)) == 0 && SameWait(exact),
            "43a: an exact capture runs no case-rename fallback read");
        Check.Require(FallbackReads(() => Check.Require(store.ReadItemRetryForSubject(PairId, generation, OtherFile,
                Fresh(new IssueItem(Side.Local, OtherFile, OtherIdentity))) is null, "43a: no episode, no capture")) == 0,
            "43a: a no-episode read runs no case-rename fallback read");
        Check.Require(FallbackReads(() => store.ReadItemRetryForSubject(PairId, generation, Respelled, respelled)) == 1,
            "43a: a respelled capture runs exactly one fallback read");

        var rename = StateChange.Empty with
        {
            Renames = [new RenameApplied(saved, saved with { LocalPath = Respelled }, null, null, null)]
        };
        var localItem = new IssueItem(Side.Local, File, Identity);
        var other = new SyncedEntry(PathNames.Key(OtherFile), OtherFile, PathNames.DropboxKey(OtherFile), OtherFile, false,
            OtherIdentity, 1, 0, "other-dropbox", "hash");
        foreach (var (wider, what) in new (StateChange Change, string What)[]
        {
            (rename with { Close = [(File, IssueClosure.AfterEffect)] }, "a close by key"),
            (rename with { FirstRunAcknowledged = [localItem] }, "a first-run acknowledgement"),
            (rename with { Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, File, held.Token.Subject)] }, "a raise"),
            (rename with { Record = [saved] }, "a content record"),
            (StateChange.Empty with { Renames = [new RenameApplied(saved, saved with
                { LocalPath = Renamed, LocalKey = PathNames.Key(Renamed) }, null, null, null)] }, "a move that is not case-only"),
            (StateChange.Empty with { Renames = [new RenameApplied(saved, saved with { LocalPath = Renamed }, null, null, null)] },
                "a change that is not case-only behind equal keys"),
            (StateChange.Empty with { Renames = [new RenameApplied(other, other with { LocalPath = "Parent/OTHER" }, null, null,
                null)] }, "another item's respelling on this wait's valid token"),
        })
            Check.Require(Refusal(() => store.CommitItemRename(held, attempt, wider, null)) is ArgumentException &&
                    store.SyncedByKey(PairId, Side.Local, saved.LocalKey)?.LocalPath == File,
                "43a: the respelling route refuses " + what + " before any write");
        // The shape rule itself, not only the store's later key check, refuses a non-case change behind equal keys.
        Check.Require(rename.RespellsWithin(held) && !(StateChange.Empty with { Renames = [new RenameApplied(saved,
                saved with { LocalPath = Renamed }, null, null, null)] }).RespellsWithin(held),
            "43a: RespellsWithin accepts only a case-only respelling of the wait's item");
        Check.Require(Refusal(() => store.CommitItemRename(held, Guid.NewGuid(), rename, null)) is LiveAdmissionStaleException,
            "43a: only the registered attempt commits the respelling");
        Check.Require(Refusal(() => store.CommitItemRename(held with { Token = held.Token with
                { Revision = held.Token.Revision + 1 } }, attempt, rename, null)) is LiveAdmissionStaleException &&
                store.SyncedByKey(PairId, Side.Local, saved.LocalKey)?.LocalPath == File,
            "43a: the respelling is claimed against the wait's exact token in its transaction");

        // A retry has no selection: the whole saved before-image is revalidated in the transaction (43a). The token is
        // this wait's exact one, so only that revalidation can refuse these.
        foreach (var (stale, what) in new (SyncedEntry Before, string What)[]
        {
            (saved with { DropboxContentHash = "stale" }, "a stale content baseline"),
            (saved with { LocalSize = saved.LocalSize + 1 }, "a stale Local size"),
        })
            Check.Require(Refusal(() => store.CommitItemRename(held, attempt, StateChange.Empty with { Renames =
                    [new RenameApplied(stale, stale with { LocalPath = Respelled }, null, null, null)] }, null)) is
                    LiveAdmissionStaleException && store.SyncedByKey(PairId, Side.Local, saved.LocalKey)?.LocalPath == File,
                "43a: the respelling refuses " + what + " before any write");
        Check.Require(Refusal(() => store.CommitItemRename(held, attempt, StateChange.Empty with { Renames =
                [new RenameApplied(saved with { DropboxId = "replaced" }, saved with { DropboxId = "replaced", LocalPath =
                    Respelled }, null, null, null)] }, null)) is ArgumentException &&
                store.SyncedByKey(PairId, Side.Local, saved.LocalKey)?.LocalPath == File,
            "43a: the respelling refuses a binding replaced on the wait's recorded Dropbox side");

        store.CommitItemRename(held, attempt, rename, null);
        var issue = store.Issues(PairId).Single();
        Check.Require(store.SyncedByKey(PairId, Side.Local, saved.LocalKey)?.LocalPath == Respelled &&
                issue.Id == held.Token.IssueId && issue.Cause == TextKey.FailLocalInUse &&
                issue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item &&
                SameWait(store.ReadItemRetryState(PairId, [issue.Id])[issue.Id]),
            "43a: the respelling is saved while the wait, its one notice and its first-run work continue");
        // After the commit the saved row matches the respelled file: the content capture finds the same wait.
        var continuing = store.ReadItemRetryForSubject(PairId, generation, Respelled, respelled);
        Check.Require(SameWait(continuing), "43a: the content attempt continues the same wait after the respelling");
        // The content success acknowledges first-run work at the fresh spelling; the notice keeps the old one.
        store.CompleteItemAttempt(continuing!, attempt, StateChange.Empty with
        {
            Close = [(Respelled, IssueClosure.AfterInSync)],
            FirstRunAcknowledged = [new IssueItem(Side.Local, Respelled, Identity)]
        }, null, null);
        Check.Require(store.ReadItemRetryState(PairId, [issue.Id])[issue.Id] is null && store.Issues(PairId).Count == 0,
            "43a: the content success ends the wait, acknowledges its first-run work and closes its notice");
    }

    // Row 43a: a wait raised before rename subjects named the saved Dropbox counterpart holds only the Local item. A
    // held case-only rename still captures it, keeps it through the respelling, and the content continues it.
    private static void CaseRenameKeepsLegacyWait(string database)
    {
        const string Respelled = "Parent/FILE";
        const string DropboxIdentity = "dropbox-identity";
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)], []);
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File, false, Identity, 1, 0,
            DropboxIdentity, "hash");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var held = store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity)], null),
            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        var renamed = new IssueSubject([new IssueItem(Side.Local, Respelled, Identity),
            new IssueItem(Side.Dropbox, File, DropboxIdentity)], null);
        // Only the held binding's own counterpart is the one endpoint the legacy shape may add (43a).
        foreach (var (stranger, what) in new (IssueItem Item, string What)[]
        {
            (new IssueItem(Side.Dropbox, File, OtherIdentity), "another Dropbox identity at the key"),
            (new IssueItem(Side.Dropbox, OtherFile, DropboxIdentity), "the counterpart's identity at another key"),
        })
        {
            Exception? refused = null;
            try { store.ReadItemRetryForSubject(PairId, generation, Respelled, renamed with { Items = [renamed.Items[0], stranger] }); }
            catch (LiveAdmissionStaleException error) { refused = error; }
            Check.Require(refused is not null, "43a: a legacy Local-only wait refuses " + what);
        }
        var captured = store.ReadItemRetryForSubject(PairId, generation, Respelled, renamed) ??
            throw new InvalidOperationException(ExpectedFailure);
        Check.Require(captured.Token.EpisodeId == held.Token.EpisodeId && captured.Token.Revision == held.Token.Revision,
            "43a: a held case-only rename captures a legacy Local-only wait");
        store.CommitItemRename(captured, attempt, StateChange.Empty with
        {
            Renames = [new RenameApplied(saved, saved with { LocalPath = Respelled }, null, null, null)]
        }, null);
        var content = new IssueSubject([new IssueItem(Side.Local, Respelled, Identity),
            new IssueItem(Side.Dropbox, File, DropboxIdentity)], null);
        var continuing = store.ReadItemRetryForSubject(PairId, generation, Respelled, content);
        Check.Require(store.SyncedByKey(PairId, Side.Local, saved.LocalKey)?.LocalPath == Respelled &&
                continuing?.Token.EpisodeId == held.Token.EpisodeId && store.Issues(PairId).Single().Id == held.Token.IssueId,
            "43a: the legacy wait and its one notice survive the respelling and continue to the content attempt");
    }
}
