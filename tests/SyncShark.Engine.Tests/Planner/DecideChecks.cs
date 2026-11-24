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
        var first = row with { Context = row.Context with { FirstRun = true, CompleteInventory = true } };
        var baseline = new SyncedEntry(PathNames.Key(local.Path), local.Path, PathNames.DropboxKey(remote.Path), remote.Path, false, local.Id, local.Size, at.UtcTicks, remote.Id, hash);
        var saved = row with { Synced = baseline, Dropbox = remote with { Size = local.Size } };
        var candidate = first with { Dropbox = null, Context = first.Context with { DeletionEligible = true, Destination = "item" } };
        var linked = saved with { Dropbox = saved.Dropbox! with { Path = "other" },
            Synced = baseline with { DropboxKey = PathNames.DropboxKey("other"), DropboxPath = "other" } };
        CheckTable(row, linked, candidate);
        Check.Require(Pick(row).Kind == ActionKind.KeepBoth, "Ordinary different files are not newest-wins");
        Check.Require(Pick(first).Kind == ActionKind.Download, "First sync clearly newer remote");
        var kinds = first with { Local = local with { IsFolder = true } };
        Check.Require(Pick(kinds).Kind == ActionKind.KeepBoth && Pick(kinds, FirstRunPolicy.LocalWins) is { Kind: ActionKind.ReplaceKind, Side: Side.Dropbox }, "First sync kind clash");
        var held = row with { Context = row.Context with { Held = true, HeldKind = NoticeKind.Tie } };
        Check.Require(Pick(held).None == NoneReason.Held, "Legacy tie survives ordinary decisions");
        Check.Require(Pick(saved).Kind == ActionKind.None && Pick(saved, dirty: true).Kind == ActionKind.NeedLocalHash, "Dirty event cannot settle from stamps");
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
    private static void CheckTable(Row pair, Row saved, Row candidate)
    {
        var failures = new List<string>();
        var local = pair.Local!;
        var cloud = pair.Dropbox!;
        var now = cloud.Modified.AddHours(1);
        void Expect(string name, Row r, ActionKind kind, string? target = null, Side? side = null, TextKey? info = null,
            NoneReason none = NoneReason.InSync, NoticeKind notice = NoticeKind.Info, TextKey cause = default,
            (Side, bool)? exclusion = null, FirstRunPolicy policy = FirstRunPolicy.NewestWins, Row? observed = null, bool dirty = false)
        {
            var action = Decide.For(r, policy, now, dirty, exclusion);
            if (action != new SyncAction(kind, observed ?? r, none, notice, cause, target, info, side)) failures.Add(name);
        }
        var unbound = pair with { Context = pair.Context with { Destination = "mapped" } };
        Expect("ordinary out of scope", pair with { Context = pair.Context with { OutOfScope = true } }, ActionKind.None, none: NoneReason.OutOfScope);
        Expect("ordinary skipped", pair with { Context = pair.Context with { Skipped = true } }, ActionKind.None, none: NoneReason.Skipped);
        Expect("ambiguous mapped names", pair with { Extra = [cloud with { Path = "Item", Id = "other-id" }] }, ActionKind.Notice,
            notice: NoticeKind.NameClash, cause: TextKey.ClashMappedNames);
        var held = pair with { Context = pair.Context with { Held = true, HeldKind = NoticeKind.Tie } };
        Expect("ordinary stored tie", held, ActionKind.None, none: NoneReason.Held);
        Expect("neither endpoint", unbound with { Local = null, Dropbox = null }, ActionKind.None);
        foreach (var folder in new[] { false, true })
        {
            Expect($"PC only (folder={folder})", unbound with { Local = local with { IsFolder = folder }, Dropbox = null },
                folder ? ActionKind.EnsureDropboxFolder : ActionKind.Upload, "mapped");
            Expect($"Dropbox only (folder={folder})", unbound with { Dropbox = cloud with { IsFolder = folder }, Local = null },
                folder ? ActionKind.EnsureLocalFolder : ActionKind.Download, "mapped");
        }
        var equalSize = pair with { Dropbox = cloud with { Size = local.Size, Modified = local.Modified } };
        Expect("ordinary equal stamps still need hash", equalSize, ActionKind.NeedLocalHash);
        Expect("ordinary equal hash", equalSize with { LocalHash = cloud.ContentHash }, ActionKind.Record);
        Expect("ordinary unequal hash", equalSize with { LocalHash = "different" }, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        var first = equalSize with { Context = equalSize.Context with { FirstRun = true } };
        var pcNewer = pair with { Local = local with { Modified = now }, Context = first.Context };
        Expect("first newest wins PC newer", pcNewer, ActionKind.Upload, cloud.Path);
        foreach (var localFolder in new[] { true, false })
        {
            var mixed = first with { Local = local with { IsFolder = localFolder }, Dropbox = cloud with { IsFolder = !localFolder } };
            Expect($"first Dropbox wins mixed (local folder={localFolder})", mixed, ActionKind.ReplaceKind, local.Path, Side.Local, policy: FirstRunPolicy.DropboxWins);
        }
        Expect("first reliable same second", first, ActionKind.Record);
        Expect("first same second different size", first with { Local = local with { Size = cloud.Size } }, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        var future = first with { Local = local with { Modified = now.AddDays(1) } };
        Expect("untrusted equal size needs hash", future, ActionKind.NeedLocalHash);
        Expect("untrusted different content", future with { LocalHash = "different" }, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        Expect("unbound folders keep spelling", pair with { Local = local with { IsFolder = true }, Dropbox = cloud with { IsFolder = true, Path = "Item" } }, ActionKind.Record);
        var folders = saved with { Local = local with { IsFolder = true }, Dropbox = saved.Dropbox! with { IsFolder = true }, Synced = saved.Synced! with { IsFolder = true } };
        Expect("saved folder unchanged", folders, ActionKind.None);
        Expect("saved folder PC spelling refresh", folders with { Local = folders.Local! with { Path = "Item" } }, ActionKind.Record);
        Expect("saved folder Dropbox spelling refresh", folders with { Dropbox = folders.Dropbox! with { Path = "Other" } }, ActionKind.Record);
        Expect("saved folder replaced by unequal-size files", folders with { Local = local, Dropbox = saved.Dropbox! with { Size = local.Size + 1 } }, ActionKind.KeepBoth, info: TextKey.InfoKeptBoth);
        Expect("cloud ID refresh", saved with { Dropbox = saved.Dropbox! with { Id = "new" } }, ActionKind.Record);
        var edited = saved with { LocalHash = new string('b', 64) };
        Expect("dirty same-size PC edit uploads", edited, ActionKind.Upload, saved.Synced!.DropboxPath, dirty: true);
        Expect("dirty same-size PC edit beats Dropbox deletion", edited with { Dropbox = null }, ActionKind.Upload,
            saved.Synced.DropboxPath, info: TextKey.InfoEditBeatsDelete, dirty: true);
        Expect("PC stamp refresh", saved with { Local = local with { Modified = now }, LocalHash = cloud.ContentHash }, ActionKind.Record);
        Expect("crossed Dropbox target", saved with { Local = local with { Size = local.Size + 1 }, Dropbox = cloud with { Id = "return" } }, ActionKind.Upload, local.Path);
        Expect("crossed PC target", saved with { Local = local with { Id = "return", Path = "other" }, LocalHash = cloud.ContentHash,
            Dropbox = saved.Dropbox! with { ContentHash = "changed" } }, ActionKind.Download, "other");
        var debt = candidate with { Context = candidate.Context with { Destination = "mapped", FirstRun = false, FirstRunDeletion = IssueSubject.FirstRunDeletionKind.Item } };
        var deleted = debt with { DeletionClock = (TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            Deletion = new DeletionEvidence.DeletedFile("mapped", "domain", cloud.Id, null, now, null, cloud.ContentHash, null) };
        Expect("later deletion with equal hash recycles", deleted with { LocalHash = cloud.ContentHash }, ActionKind.DeleteLocal, local.Path);
        Expect("later deletion with differing hash preserves edit", deleted with { LocalHash = new string('b', 64) }, ActionKind.Upload, "mapped", info: TextKey.InfoEditBeatsDelete);
        var knownLocal = deleted with { LocalHash = cloud.ContentHash };
        Expect("missing deletion time preserves file", knownLocal with { Deletion = new DeletionEvidence.DeletedFile(
            "mapped", "domain", cloud.Id, null, null, TextKey.DeletionTimeMissing, cloud.ContentHash, null) },
            ActionKind.Upload, "mapped", info: TextKey.InfoEditBeatsDelete);
        Expect("missing historical digest preserves file", knownLocal with { Deletion = new DeletionEvidence.DeletedFile(
            "mapped", "domain", cloud.Id, null, now, null, null, TextKey.DeletionHashMissing) },
            ActionKind.Upload, "mapped", info: TextKey.InfoEditBeatsDelete);
        var clock = (Offset: TimeSpan.FromMinutes(2), Uncertainty: TimeSpan.FromSeconds(1));
        var serverTime = local.Modified + clock.Offset;
        foreach (var (label, time) in new[] { ("earlier", serverTime.AddSeconds(-2)), ("at uncertainty boundary", serverTime + clock.Uncertainty) })
        {
            var unproven = debt with { LocalHash = cloud.ContentHash, DeletionClock = clock,
                Deletion = new DeletionEvidence.DeletedFile("mapped", "domain", cloud.Id, null, time, null, cloud.ContentHash, null) };
            Expect($"equal hash, deletion {label}", unproven, ActionKind.Upload, "mapped", info: TextKey.InfoEditBeatsDelete);
        }
        Expect("evidence lookup target", debt, ActionKind.NeedDeletionEvidence, "mapped");
        Expect("retained preservation notice", debt with { Context = debt.Context with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice } }, ActionKind.Upload, "mapped", info: TextKey.InfoEditBeatsDelete);
        foreach (var evidence in new DeletionEvidence[] { new DeletionEvidence.Unknown("mapped", TextKey.FailChanged),
            new DeletionEvidence.NoRetainedHistory("mapped") })
            Expect(evidence.GetType().Name, debt with { Deletion = evidence }, ActionKind.Upload, "mapped", info: TextKey.InfoEditBeatsDelete);
        Expect("unreadable Dropbox history", debt with { Deletion = new DeletionEvidence.HistoryUnavailable("mapped", TextKey.ReadDropboxFailed) },
            ActionKind.Upload, "mapped", info: TextKey.InfoDropboxHistoryUnchecked);
        var live = cloud with { Path = "mapped", Skip = nameof(TextKey.SkipNotDownloadable) };
        Expect("live elsewhere", debt with { Deletion = new DeletionEvidence.LiveElsewhere("mapped", new("domain", "other", cloud)) },
            ActionKind.Notice, notice: NoticeKind.Failure, cause: TextKey.FailChanged);
        Expect("mismatched evidence target", debt with { Deletion = new DeletionEvidence.NoRetainedHistory("other") },
            ActionKind.Notice, notice: NoticeKind.Failure, cause: TextKey.FailChanged);
        foreach (var policy in new[] { FirstRunPolicy.NewestWins, FirstRunPolicy.LocalWins, FirstRunPolicy.DropboxWins })
        foreach (var entry in new[] { live, live with { Size = local.Size } })
        {
            var retry = debt with { Deletion = new DeletionEvidence.LiveAtTarget("mapped", new("domain", "mapped", entry)) };
            Expect($"skipped historical counterpart ({policy},size={entry.Size})", retry, ActionKind.None,
                none: NoneReason.Skipped, policy: policy, observed: retry with { Deletion = null, Dropbox = entry });
        }
        var readable = live with { Skip = null, Size = local.Size };
        var retained = debt with { Deletion = new DeletionEvidence.LiveAtTarget("mapped", new("domain", "mapped", readable)) };
        foreach (var policy in new[] { FirstRunPolicy.DropboxWins, FirstRunPolicy.LocalWins, FirstRunPolicy.NewestWins })
        {
            var requested = Decide.For(retained, policy, now, false);
            Check.Require(requested.Kind == ActionKind.NeedLocalHash && requested.Row.Dropbox == readable && requested.Row.Deletion is null,
                "Retained retry must request a hash on the refreshed row");
            var hashed = requested.Row with { LocalHash = edited.LocalHash };
            if (policy == FirstRunPolicy.LocalWins) Expect("retained PC wins after hash", hashed, ActionKind.Upload, readable.Path, policy: policy);
            else Expect($"retained {policy} after hash", hashed, ActionKind.Download, local.Path, policy: policy);
        }
        var junction = saved with { Local = local with { IsFolder = true, Skip = nameof(TextKey.SkipLink) },
            Dropbox = cloud with { IsFolder = true }, Synced = null, Context = saved.Context with { Skipped = true } };
        Expect("kept PC junction is not a preserved folder", junction, ActionKind.None, none: NoneReason.Skipped, exclusion: (Side.Dropbox, false));
        var stub = saved with { Local = local with { Skip = nameof(TextKey.SkipOnlineOnly) }, Context = saved.Context with { Skipped = true } };
        Expect("settled source replaces kept PC stub", stub, ActionKind.Download, local.Path, exclusion: (Side.Dropbox, false));
        Expect("excluded PC stub protected", stub, ActionKind.Notice, notice: NoticeKind.Failure, cause: TextKey.FailExcludeHoldsProtected, exclusion: (Side.Local, false));
        foreach (var excluded in new[] { Side.Local, Side.Dropbox })
        {
            var kept = PathNames.Other(excluded);
            var mixed = saved with { Local = local with { IsFolder = true } };
            Expect("owed kind clash " + excluded, mixed, ActionKind.Notice, notice: NoticeKind.NameClash, cause: TextKey.ClashFileFolder, exclusion: (excluded, true));
            Expect("settled kind replacement " + excluded, mixed, ActionKind.ReplaceKind, mixed.EntryOn(kept)!.Path, kept, exclusion: (excluded, false));
            var missing = excluded == Side.Local ? saved with { Local = null } : saved with { Dropbox = null };
            Expect("no excluded source " + excluded, missing, ActionKind.None, none: NoneReason.OutOfScope, exclusion: (excluded, false));
            var lonely = excluded == Side.Local ? saved with { Dropbox = null } : saved with { Local = null };
            Expect("no kept copy " + excluded, lonely, kept == Side.Local ? ActionKind.Download : ActionKind.Upload, saved.Synced!.PathOn(kept), exclusion: (excluded, false));
        }
        Check.Require(failures.Count == 0, "Wrong planner decisions: " + string.Join("; ", failures));
    }
}
