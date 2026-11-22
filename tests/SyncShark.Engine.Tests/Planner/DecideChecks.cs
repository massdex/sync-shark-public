using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Exercises the ordinary table, deferred folder decision, server deletion proof and exclusion overrides.</summary>
public static class DecideChecks
{
    public static Task RunAsync()
    {
        var at = DateTimeOffset.UnixEpoch;
        var hash = new string('a', 64);
        var local = new Entry("item", false, 1, at, "pc-id", "", hash, null);
        var remote = local with { Id = "cloud-id", Modified = at.AddMinutes(1), Size = 2 };
        var row = new Row("item", local, remote, null, [], null, RowContext.Neutral);
        SyncAction Pick(Row r, FirstRunPolicy policy = FirstRunPolicy.NewestWins, (Side, bool)? exclusion = null, bool dirty = false) =>
            Decide.For(r, policy, at.AddHours(1), dirty, exclusion);
        Check.Require(Pick(row).Kind == ActionKind.KeepBoth, "Ordinary different files are not newest-wins");
        var first = row with { Context = row.Context with { FirstRun = true, CompleteInventory = true } };
        Check.Require(Pick(first).Kind == ActionKind.Download, "First sync clearly newer remote");
        var kinds = first with { Local = local with { IsFolder = true } };
        Check.Require(Pick(kinds).Kind == ActionKind.KeepBoth && Pick(kinds, FirstRunPolicy.LocalWins) is { Kind: ActionKind.ReplaceKind, Side: Side.Dropbox }, "First sync kind clash");
        var held = row with { Context = row.Context with { Held = true, HeldKind = NoticeKind.Tie } };
        Check.Require(Pick(held).None == NoneReason.Held, "Legacy tie survives ordinary decisions");
        var baseline = new SyncedEntry("item", "item", "item", "item", false, local.Id, local.Size, at.UtcTicks, remote.Id, hash);
        var saved = row with { Synced = baseline, Dropbox = remote with { Size = local.Size } };
        Check.Require(Pick(saved).Kind == ActionKind.None && Pick(saved, dirty: true).Kind == ActionKind.NeedLocalHash, "Dirty event cannot settle from stamps");
        var candidate = first with { Dropbox = null, Context = first.Context with { DeletionEligible = true, Destination = "item" } };
        var folder = candidate with { Local = local with { IsFolder = true } };
        Check.Require(Pick(folder).Kind == ActionKind.DeferFolder && Pick(folder with { Context = folder.Context with { CompleteInventory = false } }).None == NoneReason.Held, "Parent row alone cannot authorize a folder delete");
        var evidence = new DeletionEvidence.DeletedFile("item", "domain", remote.Id, null, at.AddMinutes(1), null, hash, null);
        var confirmed = candidate with { Deletion = evidence, LocalHash = hash, DeletionClock = (TimeSpan.Zero, TimeSpan.FromSeconds(1)) };
        Check.Require(Pick(confirmed).Kind == ActionKind.DeleteLocal && Pick(confirmed with { LocalHash = null }).Kind == ActionKind.NeedLocalHash, "Only matching proven content can be deleted");
        var preserve = confirmed with { Context = confirmed.Context with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice } };
        Check.Require(Pick(preserve).Kind == ActionKind.Upload, "Retained preservation must never become a delete");
        Check.Require(Pick(confirmed with { DeletionClock = null }) is { Kind: ActionKind.Upload, InfoAfter: TextKey.InfoEditBeatsDelete }, "Unknown clock preserves local version");
        Check.Require(Pick(kinds, exclusion: (Side.Local, true)) is { Kind: ActionKind.Notice, Cause: TextKey.ClashFileFolder }, "Owed exclusion keeps kind clash");
        Check.Require(Pick(kinds, exclusion: (Side.Local, false)) is { Kind: ActionKind.ReplaceKind, Side: Side.Dropbox }, "Re-created exclusion replaces kept side");
        Check.Require(Pick(row, exclusion: (Side.Dropbox, true)) is { Kind: ActionKind.KeepBoth, Side: Side.Local }, "Owed conflict uses only the kept leg");
        var linked = saved with { Dropbox = saved.Dropbox! with { Path = "other" },
            Synced = baseline with { DropboxKey = "other", DropboxPath = "other" } };
        SavedAndExcluded(linked);
        HistoricalAndPlaceholder(first, candidate);
        return Task.CompletedTask;
    }
    private static void SavedAndExcluded(Row saved)
    {
        var local = saved.Local!;
        var cloud = saved.Dropbox!;
        var changedLocal = local with { Size = local.Size + 1 };
        var changedCloud = cloud with { ContentHash = new string('b', 64) };
        Check.Require(!Decide.MayCopyFile(local, saved.Synced, Side.Local) && !Decide.MayCopyFile(cloud, saved.Synced, Side.Dropbox)
            && Decide.MayCopyFile(changedCloud, saved.Synced, Side.Dropbox) && Decide.MayCopyFile(local with { Id = "replacement" }, saved.Synced, Side.Local)
            && Decide.MayCopyFile(local, null, Side.Local), "Owed copy eligibility distinguishes unchanged, unbound and unknown content");
        var folderLocal = local with { IsFolder = true };
        var folderCloud = cloud with { IsFolder = true };
        void Expect(Row r, ActionKind kind, string? target = null, Side? side = null, TextKey? info = null,
            (Side, bool)? exclusion = null, NoneReason none = NoneReason.InSync)
        {
            var actual = Decide.For(r, FirstRunPolicy.NewestWins, cloud.Modified, false, exclusion);
            Check.Require(actual == new SyncAction(kind, r, none, Target: target, Side: side, InfoAfter: info),
                $"Expected {kind}/{side}/{target}/{info}/{none}, got {actual.Kind}/{actual.Side}/{actual.Target}/{actual.InfoAfter}/{actual.None}");
        }
        Expect(saved with { Local = null, Dropbox = null }, ActionKind.ForgetOnly);
        Expect(saved with { Local = null }, ActionKind.DeleteDropbox);
        Expect(saved with { Dropbox = null }, ActionKind.DeleteLocal);
        Expect(saved with { Local = changedLocal }, ActionKind.Upload, cloud.Path);
        Expect(saved with { Dropbox = changedCloud }, ActionKind.Download, local.Path);
        Expect(saved with { Local = null, Dropbox = changedCloud }, ActionKind.Download, local.Path, info: TextKey.InfoEditBeatsDelete);
        Expect(saved with { Local = changedLocal, Dropbox = null }, ActionKind.Upload, cloud.Path, info: TextKey.InfoEditBeatsDelete);
        foreach (var other in new Entry?[] { null, cloud })
            Expect(saved with { Local = folderLocal, Dropbox = other }, ActionKind.ReplaceKind, cloud.Path, Side.Dropbox);
        foreach (var other in new Entry?[] { null, local })
            Expect(saved with { Local = other, Dropbox = folderCloud }, ActionKind.ReplaceKind, local.Path, Side.Local);
        Expect(saved with { Local = folderLocal, Dropbox = changedCloud }, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        Expect(saved with { Local = changedLocal, Dropbox = folderCloud }, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        Expect(saved with { Local = folderLocal, Dropbox = folderCloud }, ActionKind.Record);
        var both = saved with { Local = changedLocal, Dropbox = changedCloud };
        Expect(both, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        var sameSize = both with { Dropbox = changedCloud with { Size = changedLocal.Size } };
        var fromFolder = saved with { Synced = saved.Synced! with { IsFolder = true } };
        foreach (var pair in new[] { sameSize, fromFolder })
        {
            Expect(pair, ActionKind.NeedLocalHash);
            Expect(pair with { LocalHash = pair.Dropbox!.ContentHash }, ActionKind.Record);
            Expect(pair with { LocalHash = new string('c', 64) }, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        }
        Expect(fromFolder with { Local = null, Dropbox = folderCloud }, ActionKind.DeferFolder);
        Expect(fromFolder with { Local = folderLocal, Dropbox = null }, ActionKind.DeferFolder);
        foreach (var excluded in new[] { Side.Local, Side.Dropbox })
        {
            var kept = PathNames.Other(excluded);
            var keptEdited = excluded == Side.Local ? saved with { Dropbox = changedCloud } : saved with { Local = changedLocal };
            var keptDeleted = excluded == Side.Local ? saved with { Dropbox = null } : saved with { Local = null };
            Expect(keptEdited, ActionKind.None, exclusion: (excluded, true), none: NoneReason.OutOfScope);
            Expect(keptDeleted, ActionKind.None, exclusion: (excluded, true), none: NoneReason.OutOfScope);
            Expect(both, ActionKind.KeepBoth, side: kept, info: TextKey.InfoKeptBoth, exclusion: (excluded, true));
            Expect(saved, ActionKind.NeedLocalHash, exclusion: (excluded, false));
            Expect(saved with { LocalHash = cloud.ContentHash }, ActionKind.Record, exclusion: (excluded, false));
            Expect(saved with { LocalHash = changedCloud.ContentHash }, kept == Side.Local ? ActionKind.Download : ActionKind.Upload,
                saved.EntryOn(kept)!.Path, exclusion: (excluded, false));
        }
    }

    private static void HistoricalAndPlaceholder(Row first, Row candidate)
    {
        var failures = new List<string>();
        void Expect(string name, Row r, ActionKind kind, string? target = null, NoneReason none = NoneReason.InSync,
            NoticeKind notice = NoticeKind.Info, TextKey cause = default, FirstRunPolicy policy = FirstRunPolicy.NewestWins)
        {
            var a = Decide.For(r, policy, first.Dropbox!.Modified.AddHours(1), false);
            if (a.Kind != kind || a.None != none || a.Target != target || a.NoticeKind != notice || a.Cause != cause)
                failures.Add($"{name}: expected {kind}/{none}/{notice}/{cause}/{target}, got {a.Kind}/{a.None}/{a.NoticeKind}/{a.Cause}/{a.Target}");
        }
        var local = first.Local!;
        var cloud = first.Dropbox!;
        var retained = candidate with { Local = local with { IsFolder = true }, Context = candidate.Context with
            { DeletionEligible = false, FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice } };
        Expect("retained folder", retained, ActionKind.DeferFolder);
        Expect("incomplete retained folder", retained with { Context = retained.Context with { CompleteInventory = false } }, ActionKind.None, none: NoneReason.Held);
        var retry = candidate with { Context = candidate.Context with { FirstRun = false, DeletionEligible = false,
            FirstRunDeletion = IssueSubject.FirstRunDeletionKind.Item }, Deletion = new DeletionEvidence.LiveAtTarget("item", new("domain", "item", cloud)) };
        Expect("retry newest", retry, ActionKind.Download, local.Path);
        Expect("retry PC wins", retry, ActionKind.Upload, cloud.Path, policy: FirstRunPolicy.LocalWins);
        Expect("retry Dropbox wins", retry, ActionKind.Download, local.Path, policy: FirstRunPolicy.DropboxWins);
        Expect("nonhistorical item", retry with { Context = retry.Context with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None } }, ActionKind.Upload, "item");
        foreach (var skipped in new[] { false, true })
        {
            var stub = first with { Local = local with { Skip = nameof(TextKey.SkipOnlineOnly) }, Context = first.Context with { Skipped = skipped } };
            Expect("readable stub counterpart", stub, ActionKind.Download, local.Path);
            Expect("absent stub counterpart", stub with { Dropbox = null }, ActionKind.Notice, notice: NoticeKind.Skipped, cause: TextKey.SkipOnlineOnly);
            Expect("unreadable stub counterpart", stub with { Dropbox = cloud with { Skip = nameof(TextKey.SkipNotDownloadable) } }, ActionKind.Notice, notice: NoticeKind.Failure, cause: TextKey.ReadDropboxFailed);
            Expect("folder is not a readable stub counterpart", stub with { Dropbox = cloud with { IsFolder = true } }, ActionKind.Notice, notice: NoticeKind.Failure, cause: TextKey.ReadDropboxFailed);
            Expect("out-of-scope stub", stub with { Context = stub.Context with { OutOfScope = true } }, ActionKind.None, none: NoneReason.OutOfScope);
        }
        Check.Require(failures.Count == 0, string.Join("; ", failures));
    }
}
