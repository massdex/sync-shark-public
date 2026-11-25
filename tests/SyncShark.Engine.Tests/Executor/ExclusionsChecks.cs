using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Exclusion preview and removal checks.</summary>
public static class ExclusionsChecks
{
    /// <summary>Verifies the Exclusions contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var settings = Scenarios.ItemChecks.Settings;
        var exclusion = new Exclusion(Side.Local, "dir", false);
        var configured = pair with { Exclusions = [exclusion] };

        {
            var exclusions = new Exclusions();
            var pc = new TestDoubles.FakeSide(Side.Local); var remote = new TestDoubles.FakeSide(Side.Dropbox);
            using var store = new TestDoubles.InMemoryStateStore();
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var absent = await exclusions.ApplyAsync(configured, settings, _ => { }, scope, exclusion, pc, remote, store, null!, null!, default);
            var keep = await exclusions.ApplyAsync(configured, settings, _ => { }, scope, exclusion with { Keep = true }, pc, remote, store, null!, null!, default);
            Check.Require(absent.Outcome == ExclusionOutcome.Applied && keep.Outcome == ExclusionOutcome.Applied && pc.Mutations.Count == 0, "Absent or kept source mutated");
        }
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var source = new TestDoubles.FakeSide(Side.Local);
            var peer = new TestDoubles.FakeSide(Side.Dropbox);
            var pc = Scenarios.ItemChecks.File(Side.Local, "dir", 1, "pc");
            var db = Scenarios.ItemChecks.File(Side.Dropbox, "mapped", 1, "db");
            source.Seed(pc, [1]); peer.Seed(db, [1]);
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(pc, db), null, null, default);
            var move = new IssueSubject.UnknownMove(db.Id, db.Path, "target", null, false);
            var subject = new IssueSubject([new(Side.Dropbox, db.Path, db.Id)], Side.Dropbox) { Move = move };
            await store.SaveNoticeAsync(pair.PairId, new(0, NoticeKind.Failure, TextKey.FailChanged, db.Path,
                subject, DateTimeOffset.UnixEpoch, false, Ignored: true), default);
            peer.ReadFailures[db.Path] = new InvalidOperationException("Preservation started during an uncertain move");
            var outcome = await new Exclusions().ApplyAsync(configured, settings, _ => { },
                new PathScope(configured, [configured], DriveType.Fixed), exclusion, source, peer, store, null!, null!, default);
            Check.Require(outcome.Outcome == ExclusionOutcome.Pending && source.Items.ContainsKey(pc.Path) &&
                source.Mutations.Count + peer.Mutations.Count == 0 && store.OpenNotices.Single().Subject.Move == move,
                "The outstanding move did not retain the mapped exclusion");
        }

        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var untouched = new TestDoubles.FakeSide(Side.Local);
            var folder = Scenarios.ItemChecks.File(Side.Local,"dir",0,"folder",true);
            untouched.Seed(folder,[]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(folder,folder with { Id="peer" }),null,null,default);
            var scope = new PathScope(configured,[configured],DriveType.Fixed);
            var exclusions = new Exclusions();
            var tie = new Notice(0, NoticeKind.Tie, TextKey.TieEqualTimes, "dir/child",
                new([new(Side.Local, "dir/child", "held")], null), DateTimeOffset.UnixEpoch, false, Ignored: true);
            await store.SaveNoticeAsync(pair.PairId, tie, default);
            var held = await exclusions.ApplyAsync(configured, settings, _ => { }, scope, exclusion, untouched,
                new TestDoubles.FakeSide(Side.Dropbox), store, null!, null!, default);
            Check.Require(held.Outcome == ExclusionOutcome.Pending && untouched.Mutations.Count == 0 &&
                untouched.Items.ContainsKey("dir") && store.OpenNotices.Single().Ignored,
                "An ignored legacy tie allowed removal of its folder");
            var protectedEntry = folder with { Path="skipped", Id="stub", IsFolder=false, Skip=nameof(TextKey.SkipOnlineOnly) };
            untouched.Seed(protectedEntry,[]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(protectedEntry,protectedEntry with { Id="remote" }),null,null,default);
            var preview = await exclusions.PreviewAsync(configured,new(Side.Local,"skipped",false),untouched,
                new TestDoubles.FakeSide(Side.Dropbox),store,scope,default);
            Check.Require(preview.BlockedByProtected && preview.CopyFiles==0 && untouched.Mutations.Count==0,"Bound protected preview allowed removal");
        }
        // A protected target itself (here a skipped root) blocks: the preview reports it, and the application is
        // refused with the required notice and the source retained, before any planner/effect decision is reached.
        var protectedSide = new TestDoubles.FakeSide(Side.Local);
        protectedSide.Seed(Scenarios.ItemChecks.File(Side.Local, "f", 1, "f") with
            { Skip = nameof(TextKey.SkipOnlineOnly) }, [1]);
        var blocked = new Exclusion(Side.Local, "f", false);
        var blockedPair = pair with { Exclusions = [blocked] };
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(protectedSide.Items["f"].Entry,
                Scenarios.ItemChecks.File(Side.Dropbox, "f", 1, "peer")), null, null, default);
            var scope = new PathScope(blockedPair, [blockedPair], DriveType.Fixed);
            var exclusions = new Exclusions();
            var preview = await exclusions.PreviewAsync(blockedPair, blocked, protectedSide,
                new TestDoubles.FakeSide(Side.Dropbox), store, scope, default);
            Check.Require(preview is { BlockedByProtected: true, CopyFiles: 0 } &&
                preview.RecoveryPlace == TextKey.RecoveryRecycleBin, "Blocked preview verdict or recovery place");
            Check.Require(protectedSide.Mutations.Count == 0, "Preview changed content");

            var outcome = await exclusions.ApplyAsync(blockedPair, settings, _ => { }, scope, blocked, protectedSide,
                new TestDoubles.FakeSide(Side.Dropbox), store, null!, null!, default);
            Check.Require(outcome.Outcome == ExclusionOutcome.Refused && outcome.Cause == TextKey.FailExcludeHoldsProtected, "Protected apply reported success instead of refusing the selected source");
            Check.Require(store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure && n.Cause == TextKey.FailExcludeHoldsProtected),
                "Blocked application raised no protected-content notice");
            Check.Require(protectedSide.Mutations.Count == 0 && protectedSide.Items.ContainsKey("f"),
                "Blocked application removed the protected source");
        }
        await KeepFollowAsync();
    }

    /// <summary>A bound kept item's own move follows its endpoint without transferring any bytes.</summary>
    public static Task KeepFollowAsync() => Storage.StoreChecks.WithImage(async (store,_) =>
    {
        var pair = Scenarios.ItemChecks.Pair() with { Exclusions = [new Exclusion(Side.Local, "k", true)] };
        var paths = new PathScope(pair, [pair], DriveType.Fixed);
        await store.SavePairAsync(pair,default);
        var local = new TestDoubles.FakeSide(Side.Local);
        var remote = new TestDoubles.FakeSide(Side.Dropbox);
        var saved = new SyncedEntry(PathNames.Key("k"), "k", PathNames.DropboxKey("k"), "k", false, "X", 1, 1, "Y", "hash");
        await store.CommitAsync(pair.PairId, null, saved, null, null, default);
        remote.Seed(Scenarios.ItemChecks.File(Side.Dropbox, "k", 1, "Y"), [1]);
        var moved = Scenarios.ItemChecks.File(Side.Local, "m", 1, "X");
        local.Seed(moved, [1]);
        pair=await store.EnrollKeepsAsync(pair,default);paths=new(pair,[pair],DriveType.Fixed);
        // Only the granted apply may follow; the planner probe must not mutate the binding (K02/K08).
        var published = 0; store.PairChanged = view => {paths=new(view,[view],DriveType.Fixed);published++;};
        var action = await MoveDecision.DecideMoveAsync(pair, Scenarios.ItemChecks.Settings, paths,
            [new RenameCandidate(Side.Local, moved, saved)], local, remote, store, false, default);
        Check.Require(action.Kind == ActionKind.None && action.None == NoneReason.OutOfScope,
            "A kept binding's own move was not settled out of scope");
        Check.Require(await store.FindAsync(pair.PairId,Side.Local,saved.LocalKey,default)==saved && published==0,"A speculative KEEP probe wrote state");
        await RowExecution.ApplyAsync(pair,Scenarios.ItemChecks.Settings,_=>{},paths,null,[new(Side.Local,moved,saved)],local,remote,store,null!,null!,default);
        Check.Require(local.Mutations.Count + remote.Mutations.Count == 0, "Following a kept move transferred or removed bytes");
        Check.Require(await store.FindAsync(pair.PairId, Side.Local, PathNames.Key("k"), default) == null &&
            await store.FindAsync(pair.PairId, Side.Local, PathNames.Key("m"), default) is {} binding &&
            binding.LocalId == "X" && binding.DropboxId == "Y", "The kept binding did not follow its endpoint");
        Check.Require(published == 1, "A committed kept-move did not publish its new live scope");
        var edit=moved with {Size=2};local.Items[edit.Path]=(edit,[2,2]);
        await ItemSync.ApplyAsync(pair,Scenarios.ItemChecks.Settings,_=>{},paths,new(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false),local,remote,store,null!,null!,default);
        Check.Require(paths.Excludes("m",null,false) && remote.Items["k"].Bytes.SequenceEqual(new byte[]{1}),"Changed content at a followed name left KEEP");
        var parent=Scenarios.ItemChecks.File(Side.Local,"folder",0,"parent",true);
        var child=Scenarios.ItemChecks.File(Side.Local,"folder/file",1,"child");
        var peer=child with {Id="peer"};var linked=SyncedEntries.From(child,peer);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(parent,parent with {Id="remote-parent"}),null,null,default);
        await store.CommitAsync(pair.PairId,null,linked,null,null,default);
        var live=(await store.PairAsync(pair.PairId,default))!;
        await store.SavePairAsync(live with {Exclusions=live.Exclusions.Append(new(Side.Local,parent.Path,true)).ToArray()},default);
        var escaped=child with {Path="outside"};local.Seed(escaped,[1]);remote.Seed(peer,[1]);
        await RowExecution.ApplyAsync(pair,Scenarios.ItemChecks.Settings,_=>{},paths,null,[new(Side.Local,escaped,linked)],local,remote,store,null!,null!,default);
        live=(await store.PairAsync(pair.PairId,default))!;
        var parentRule=live.Exclusions.Single(e=>e.Side==Side.Local && e.Path==parent.Path);
        await store.SavePairAsync(live with {Exclusions=live.Exclusions.Where(e=>e.KeptBy?.Id!=parentRule.KeptBy?.Id).ToArray()},default,selected:parentRule);
        Check.Require(paths.Excludes("outside",null,false) && !paths.Excludes("folder/sibling",null,false),"Include of a parent exposed a departed bound child");
        // A case-only rename of a bound kept item is a real move (folded-equal, ordinal-different) and must follow (K02/K08).
        {
            var casedPair = Scenarios.ItemChecks.Pair() with { Exclusions = [new Exclusion(Side.Local, "k", true)] };
            var casedPaths = new PathScope(casedPair, [casedPair], DriveType.Fixed);
            using var casedStore = new TestDoubles.InMemoryStateStore();
            var casedLocal = new TestDoubles.FakeSide(Side.Local);
            var casedRemote = new TestDoubles.FakeSide(Side.Dropbox);
            var savedCased = new SyncedEntry(PathNames.Key("k"), "k", PathNames.DropboxKey("k"), "k", false, "X", 1, 1, "Y", "hash");
            await casedStore.CommitAsync(casedPair.PairId, null, savedCased, null, null, default);
            await casedStore.SavePairAsync(casedPair, default);
            casedRemote.Seed(Scenarios.ItemChecks.File(Side.Dropbox, "k", 1, "Y"), [1]);
            var renamedCased = Scenarios.ItemChecks.File(Side.Local, "K", 1, "X");
            casedLocal.Seed(renamedCased, [1]);
            var cased = await MoveDecision.DecideMoveAsync(casedPair, Scenarios.ItemChecks.Settings, casedPaths,
                [new RenameCandidate(Side.Local, renamedCased, savedCased)], casedLocal, casedRemote, casedStore, false, default);
            Check.Require(cased.Kind == ActionKind.None && cased.None == NoneReason.OutOfScope,
                "A case-only kept rename was not followed");
            await RowExecution.ApplyAsync(casedPair,Scenarios.ItemChecks.Settings,_=>{},casedPaths,null,[new(Side.Local,renamedCased,savedCased)],casedLocal,casedRemote,casedStore,null!,null!,default);
            Check.Require(await casedStore.FindAsync(casedPair.PairId, Side.Local, PathNames.Key("K"), default) is {} casedBinding &&
                casedBinding.LocalPath == "K" && casedBinding.LocalId == "X", "A case-only kept rename did not settle the new spelling");
        }
    });

    /// <summary>A retained exclusion keeps its completed copy, selected source and owed scope.</summary>
    public static async Task RemovalProofAsync()
    {
        var rule = new Exclusion(Side.Dropbox, "item", false);
        var debt = new Scope("item", "item");
        var pair = Scenarios.ItemChecks.Pair() with { Exclusions = [rule] };
        using var store = new TestDoubles.InMemoryStateStore();
        await store.SavePairAsync(pair, default, [(pair.PairId, debt)]);
        pair = (await store.PairAsync(pair.PairId, default))!;
        var local = new TestDoubles.FakeSide(Side.Local); var remote = new TestDoubles.FakeSide(Side.Dropbox);
        var source = Scenarios.ItemChecks.File(Side.Dropbox, "item", 3, "source");
        remote.Seed(source, [3]);
        local.Pages.Enqueue(new([], null, new(1, 30))); remote.Pages.Enqueue(new([], "boundary", null));
        var outcome = await new Exclusions().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { },
            new PathScope(pair, [pair], DriveType.Fixed), rule, local, remote, store, null!, null!, default,
            _ => [], _ => false);
        Check.Require(outcome.Outcome == ExclusionOutcome.Pending && remote.Requests[nameof(ISide.DeleteAsync)] == 0 &&
            remote.Items.TryGetValue("item", out var keptSource) && keptSource.Entry == source && keptSource.Bytes.SequenceEqual(new byte[]{3}) &&
            local.Items.TryGetValue("item", out var copy) && copy.Bytes.SequenceEqual(keptSource.Bytes) &&
            store.Records.Values.Single() == SyncedEntries.From(copy.Entry,source) &&
            (await store.PairAsync(pair.PairId,default))?.OwedScopes?.Contains(debt) == true,
            "Exclusion removal without proof lost the source, completed copy or owed scope");
    }

    /// <summary>Held and protected mapped footprints never permit removal.</summary>
    public static async Task ApplyGuardsAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var settings = Scenarios.ItemChecks.Settings;
        var exclusion = new Exclusion(Side.Local, "dir", false);
        var configured = pair with { Exclusions = [exclusion] };
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var rule = new Exclusion(Side.Dropbox, "dir", false);
            var configuredPeer = pair with { Exclusions = [rule] };
            var source = new TestDoubles.FakeSide(Side.Dropbox);
            var peer = new TestDoubles.FakeSide(Side.Local);
            source.Seed(Scenarios.ItemChecks.File(Side.Dropbox, "dir", 0, "cloud", true), []);
            peer.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "link", true) with { Skip = nameof(TextKey.SkipLink) }, []);
            var outcome = await new Exclusions().ApplyAsync(configuredPeer, settings, _ => { },
                new PathScope(configuredPeer, [configuredPeer], DriveType.Fixed), rule, peer, source, store, null!, null!, default);
            Check.Require(outcome.Outcome == ExclusionOutcome.Pending && source.Items.ContainsKey("dir") &&
                source.Mutations.Count + peer.Mutations.Count == 0, "Kept-side link permitted source removal");
        }

        // Keep exclusions and an absent target are already applied and touch nothing.
        var untouched = new TestDoubles.FakeSide(Side.Local);
        untouched.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", folder: true), []);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(untouched.Items["dir"].Entry,
                Scenarios.ItemChecks.File(Side.Dropbox, "dir", 0, "peer", true)), null, null, default);
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var exclusions = new Exclusions();
            var preview = await exclusions.PreviewAsync(configured, exclusion, untouched,
                new TestDoubles.FakeSide(Side.Dropbox), store, scope, default);
            Check.Require(!preview.BlockedByProtected && preview.RemoveFiles == 0 &&
                untouched.Requests[nameof(ISide.ReadAllAsync)] == 1, "Preview listed an empty folder more than once");
            var kept = await exclusions.ApplyAsync(configured, settings, _ => { }, scope, new Exclusion(Side.Local, "dir", true),
                untouched, new TestDoubles.FakeSide(Side.Dropbox), store, null!, null!, default);
            var absent = await exclusions.ApplyAsync(configured, settings, _ => { }, scope,
                new Exclusion(Side.Local, "missing", false), untouched, new TestDoubles.FakeSide(Side.Dropbox), store,
                null!, null!, default);
            Check.Require(kept.Outcome == ExclusionOutcome.Applied && absent.Outcome == ExclusionOutcome.Applied &&
                untouched.Mutations.Count == 0, "Keep or absent exclusion was not applied without effect");

        }
    }

    /// <summary>Protect the mapped peer even when neither the child link nor a listed descendant exists.</summary>
    public static async Task MappedProtectionAsync()
    {
        var exclusion = new Exclusion(Side.Local, "P/Sub", false);
        var pair = Scenarios.ItemChecks.Pair() with { Exclusions = [exclusion] };
        var nested = pair with { PairId = "nested", LocalRoot = @"T:\Separate", DropboxRoot = pair.DropboxRoot + "/Q/Sub/Inside" };
        var scope = new PathScope(pair, [pair, nested], DriveType.Fixed);
        using var store = new TestDoubles.InMemoryStateStore();
        var parent = SyncedEntries.From(Scenarios.ItemChecks.File(Side.Local, "P", 0, "pc-parent", true),
            Scenarios.ItemChecks.File(Side.Dropbox, "Q", 0, "db-parent", true));
        await store.CommitAsync(pair.PairId, null, parent, null, null, default);
        var local = new TestDoubles.FakeSide(Side.Local);
        var peer = new TestDoubles.FakeSide(Side.Dropbox);
        local.Seed(Scenarios.ItemChecks.File(Side.Local, exclusion.Path, 0, "child", true), []);
        var exclusions = new Exclusions();
        var preview = await exclusions.PreviewAsync(pair, exclusion, local, peer, store, scope, default);
        Check.Require(preview.BlockedByProtected, "An unbound child missed the nested root under its mapped peer");
        var result = await exclusions.ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, scope, exclusion,
            local, peer, store, null!, null!, default);
        Check.Require(result.Outcome == ExclusionOutcome.Refused && local.Items.ContainsKey(exclusion.Path) &&
            peer.Mutations.Count == 0, "Mapped protection permitted a removal or a copy");
    }

    /// <summary>A proposed exclusion previews initial preservation, not replacement of a re-created file.</summary>
    public static async Task CopyEstimateAsync()
    {
        var proposed = new Exclusion(Side.Local, "dir", false);
        var candidate = Scenarios.ItemChecks.Pair();
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var source = new TestDoubles.FakeSide(Side.Local); var peer = new TestDoubles.FakeSide(Side.Dropbox);
            source.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", true), []);
            source.Seed(Scenarios.ItemChecks.File(Side.Local, "dir/file", 1, "child"), [1]);
            peer.Seed(Scenarios.ItemChecks.File(Side.Dropbox, "dir", 2, "file"), [2]);
            peer.ReadFailures["dir/file"] = new InvalidOperationException("Preview read below a blocked folder");
            var preview = await new Exclusions().PreviewAsync(candidate, proposed, source, peer, store,
                new PathScope(candidate, [candidate], DriveType.Fixed), default);
            Check.Require(preview.CopyFiles == 0 && preview.CopyBytes == 0, "A blocked folder advertised descendant copies");
        }
        foreach (var folder in new[]{false, true})
        {
            var exclusion = new Exclusion(Side.Local, "item", false);
            var pair = Scenarios.ItemChecks.Pair();
            var scope = new PathScope(pair, [pair], DriveType.Fixed);
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local);
            var peer = new TestDoubles.FakeSide(Side.Dropbox);
            local.Seed(Scenarios.ItemChecks.File(Side.Local, "item", 1, "pc"), [1]);
            peer.Seed(Scenarios.ItemChecks.File(Side.Dropbox, "item", 2, "db", folder), folder ? [] : [2]);
            var preview = await new Exclusions().PreviewAsync(pair, exclusion, local, peer, store, scope, default);
            Check.Require(preview.CopyFiles == (folder ? 0 : 1) && preview.CopyBytes == preview.CopyFiles &&
                preview is { RemoveFiles: 1, RemoveBytes: 1 } &&
                local.Requests[nameof(ISide.ContentHashAsync)] == 0 && local.Mutations.Count + peer.Mutations.Count == 0,
                "Preview omitted a potential file copy or changed the endpoints");
        }
    }

    /// <summary>A failed source observation leaves its scope owed and publishes one typed failure on repeated attempts.</summary>
    public static async Task ReadFailureAsync()
    {
        var exclusion = new Exclusion(Side.Local, "item", false);
        var pair = Scenarios.ItemChecks.Pair() with { Exclusions = [exclusion], OwedScopes = [new("item", "item")] };
        var scope = new PathScope(pair, [pair], DriveType.Fixed);
        var local = new TestDoubles.FakeSide(Side.Local);
        local.ReadFailures[exclusion.Path] = new ItemFailedException(exclusion.Path, TextKey.ReadLocalFailed, null);
        using var store = new TestDoubles.InMemoryStateStore();
        await store.SavePairAsync(pair, default, [(pair.PairId, new Scope("item", "item"))]);
        for (int i = 0; i < 2; i++)
        {
            var result = await new Exclusions().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, scope,
                exclusion, local, new TestDoubles.FakeSide(Side.Dropbox), store, null!, null!, default);
            Check.Require(result.Outcome == ExclusionOutcome.Pending && result.Cause == TextKey.ReadLocalFailed && store.OpenNotices.Single() is
                { Kind: NoticeKind.Failure, Cause: TextKey.ReadLocalFailed, Path: "item" } &&
                (await store.PairAsync(pair.PairId, default))?.OwedScopes?.Count == 1 && local.Mutations.Count == 0,
                "A failed exclusion lost its retry, duplicated its notice, or removed its source");
        }
    }
}
