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
        Check.Require(Pick(confirmed with { DeletionClock = null }) is { Kind: ActionKind.Upload, InfoAfter: TextKey.InfoEditBeatsDelete }, "Unknown clock preserves local version");
        Check.Require(Pick(kinds, exclusion: (Side.Local, true)) is { Kind: ActionKind.Notice, Cause: TextKey.ClashFileFolder }, "Owed exclusion keeps kind clash");
        Check.Require(Pick(kinds, exclusion: (Side.Local, false)) is { Kind: ActionKind.ReplaceKind, Side: Side.Dropbox }, "Re-created exclusion replaces kept side");
        Check.Require(Pick(row, exclusion: (Side.Dropbox, true)) is { Kind: ActionKind.KeepBoth, Side: Side.Local }, "Owed conflict uses only the kept leg");
        return Task.CompletedTask;
    }
}
