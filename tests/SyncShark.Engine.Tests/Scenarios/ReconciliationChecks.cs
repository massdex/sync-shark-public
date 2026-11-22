using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Exercises the whole pair comparison; the oracle checks bytes, exact spelling, bindings and notice causes independently.</summary>
public static class ReconciliationChecks
{
    private const string Original = "item";
    internal sealed record FileAt(string Path, byte Value, bool Folder = false);
    private static void Seed(FakeSide side, IEnumerable<FileAt> entries)
    {
        foreach (var f in entries)
        {
            var entry = ItemChecks.File(side.Side, f.Path, f.Value, side.Side.ToString(), f.Folder);
            side.Seed(entry, f.Folder ? [] : [f.Value]);
        }
    }
    internal static async Task CompareAsync(bool baseline, FileAt[] pc, FileAt[] cloud, FileAt[] finalPc, FileAt[] finalCloud,
        int links, TextKey[] notices, FirstRunPolicy policy = FirstRunPolicy.NewestWins, string[]? ignores = null, Exclusion[]? exclusions = null)
    {
        var pair = ItemChecks.Pair() with { FirstRun = policy, IgnoreRules = ignores ?? [], Exclusions = exclusions ?? [] };
        var local = new FakeSide(Side.Local); var remote = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore(); Seed(local,pc); Seed(remote,cloud);
        await store.SavePairAsync(pair, default);
        if (baseline) await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        if (baseline) await store.CommitAsync(pair.PairId, null, SyncedEntries.From(
            ItemChecks.File(Side.Local, Original, 1, Side.Local.ToString()), ItemChecks.File(Side.Dropbox, Original, 1, Side.Dropbox.ToString())),null,null,default);
        await Repair.RunAsync(pair,new PathScope(pair,[pair],DriveType.Fixed),new WorkQueue(), local, remote, store, null,null, default);
        VerifySide(local,finalPc); VerifySide(remote,finalCloud);
        Check.Require(store.Records.Count == links, "Unexpected binding count");
        foreach (var binding in store.Records.Values)
        {
            var a = local.Items[binding.LocalPath].Entry; var b = remote.Items[binding.DropboxPath].Entry;
            Check.Require(binding == SyncedEntries.From(a,b), "The saved paths, identities or versions do not match both live endpoints");
        }
        Check.Require(store.OpenNotices.Select(n=>n.Cause).Order().SequenceEqual(notices.Order()), "Unexpected notices");
    }
    private static void VerifySide(FakeSide side, FileAt[] expected)
    {
        Check.Require(side.Items.Count == expected.Length, $"Unexpected entries on {side.Side}");
        foreach (var f in expected)
        {
            Check.Require(side.Items.TryGetValue(f.Path,out var v) && v.Entry.Path == f.Path && v.Entry.IsFolder == f.Folder &&
                v.Bytes.SequenceEqual(f.Folder ? Array.Empty<byte>() : new byte[]{f.Value}), $"Wrong spelling, kind or bytes on {side.Side} at {f.Path}");
        }
    }
    public static Task MoveAndEditAsync() => CompareAsync(true, [new("renamed",2)], [new(Original,1)], [new("renamed",2)], [new("renamed",2)], 1, []);
    public static Task MoveAndDeleteAsync() => CompareAsync(true, [new("renamed",1)], [], [new("renamed",1)], [new("renamed",1)], 1, [TextKey.InfoMovedDeleted]);
    public static Task MoveAndEditOppositeAsync() => CompareAsync(true, [new("renamed",1)], [new(Original,2)], [new("renamed",1),new(Original,2)], [new("renamed",1),new(Original,2)], 2, [TextKey.InfoMovedEdited]);
    public static Task DifferentDestinationsAsync() => CompareAsync(true, [new("pc-place",1)], [new("cloud-place",1)], [new("pc-place",1),new("cloud-place",1)], [new("pc-place",1),new("cloud-place",1)], 2, [TextKey.InfoMovedTwice]);
    public static Task MoveOnlyAsync() => CompareAsync(true, [new("renamed",1)], [new(Original,1)], [new("renamed",1)], [new("renamed",1)], 1, []);
    public static Task CapitalsAsync() => CompareAsync(true, [new("ITEM",1)], [new(Original,1)], [new("ITEM",1)], [new("ITEM",1)], 1, []);
    public static Task DifferentCapitalsAsync() => CompareAsync(true, [new("ITEM",1)], [new("Item",1)], [new("ITEM",1)], [new("Item",1)], 1, [TextKey.InfoCapitalsDiffer]);
    public static Task FolderCapitalsAsync() => CompareAsync(false, [new("Folder",0,true)], [new("folder",0,true)], [new("Folder",0,true)], [new("folder",0,true)], 1, []);
    public static Task FirstRunAsync(FirstRunPolicy policy, byte winner) => CompareAsync(false, [new(Original,2)], [new(Original,4)], [new(Original,winner)], [new(Original,winner)], 1, [], policy);
    public static Task IgnoreAsync() => CompareAsync(false, [new("~$draft",1)], [], [new("~$draft",1)], [], 0, [], ignores:["FileNamePrefix:~$"]);
    public static Task ExcludeKeepAsync() => CompareAsync(false, [new("outside",1)], [], [new("outside",1)], [], 0, [], exclusions:[new(Side.Local,"outside",true)]);
    public static Task ForbiddenNameAsync() => CompareAsync(false, [], [new("CON",1)], [], [new("CON",1)], 0, [TextKey.SkipReservedName]);
    public static Task KindReplacementAsync() => CompareAsync(true, [new(Original,0,true)], [new(Original,1)], [new(Original,0,true)], [new(Original,0,true)], 1, []);

    public static Task MoveOutsideAsync() => CompareAsync(true,[new("held/item",1)],[new(Original,1)], [new("held/item",1)], [], 0, [], exclusions:[new(Side.Local,"held",true)]);

    public static Task A02ChildlessFolderCreateLocalAsync() => throw new NotImplementedException();
    public static Task A02ChildlessFolderCreateCloudAsync() => throw new NotImplementedException();
    public static Task A02PopulatedFolderCreateLocalAsync() => throw new NotImplementedException();
    public static Task A02PopulatedFolderCreateCloudAsync() => throw new NotImplementedException();
    public static Task A02OutsideFolderMovedInWithoutChildReportsAsync() => throw new NotImplementedException();
    public static Task A02RestartAfterCreateBeforeListingAsync() => throw new NotImplementedException();
    public static Task A02AddOnlyPopulationNeverDeletesAsync() => throw new NotImplementedException();
    public static Task A03EqualTrustedTimesAsync() => throw new NotImplementedException();
    public static Task A03UnequalTrustedTimesAsync() => throw new NotImplementedException();
    public static Task A03ResolveUsePcAsync() => throw new NotImplementedException();
    public static Task A03ResolveUseDropboxAsync() => throw new NotImplementedException();
    public static Task A03ResolveKeepBothAsync() => throw new NotImplementedException();
    public static Task A03ResolveRenamePcAsync() => throw new NotImplementedException();
    public static Task A03ResolveRenameDropboxAsync() => throw new NotImplementedException();
    public static Task A03FourEndpointNoticeAsync() => throw new NotImplementedException();
    public static Task A05SameIdSizeMtimeWriteAfterPublicationAsync() => throw new NotImplementedException();
    public static Task A05SameWriteAtPublicationBoundaryAsync() => throw new NotImplementedException();
    public static Task A07DifferentFileEditsAsync() => throw new NotImplementedException();
    public static Task A07TwoKindFlipsToIdenticalFilesAsync() => throw new NotImplementedException();
    public static Task A07TwoKindFlipsToDifferentFilesAsync() => throw new NotImplementedException();
    public static Task A20ResolveKeepPcPlaceAsync() => throw new NotImplementedException();
    public static Task A20ResolveKeepDropboxPlaceAsync() => throw new NotImplementedException();
    public static Task A20ResolveKeepBothPlacesAsync() => throw new NotImplementedException();
    public static Task A20ChoiceSeesReplacedPcOccupantAsync() => throw new NotImplementedException();
    public static Task A20ChoiceSeesReplacedCloudOccupantAsync() => throw new NotImplementedException();
    public static Task A22MoveOntoDifferentFileAsync() => throw new NotImplementedException();
    public static Task A22CreateOntoIdenticalFileAsync() => throw new NotImplementedException();
    public static Task A22CreateOntoDifferentFileAsync() => throw new NotImplementedException();
    public static Task A22ObsoleteSourceRecoveryAsync() => throw new NotImplementedException();
    public static Task A24MovedInUnchangedCounterpartAsync() => throw new NotImplementedException();
    public static Task A24MovedInWithEditedCounterpartAsync() => throw new NotImplementedException();
    public static Task A24MovedOutWithOldRecordAsync() => throw new NotImplementedException();
    public static Task A24KeptCounterpartLeftAtExcludedOriginAsync() => throw new NotImplementedException();
    public static Task A26AutosyncIdenticalBytesAsync() => throw new NotImplementedException();
    public static Task A26AutosyncCounterpartEditedAsync() => throw new NotImplementedException();
    public static Task A26RepairSafeSaveAsync() => throw new NotImplementedException();
    public static Task A26RepairIdenticalBytesAsync() => throw new NotImplementedException();
    public static Task A26RepairCounterpartEditedAsync() => throw new NotImplementedException();
    public static Task A27CloudOnlyCapitalRenameAsync() => throw new NotImplementedException();
    public static Task A27BothPhysicalCaseHopsAsync() => throw new NotImplementedException();
    public static Task A27UnavailableRemainingHopAsync() => throw new NotImplementedException();
    public static Task A27NoTemporaryBindingAsync() => throw new NotImplementedException();
    public static Task A28ExcludeKeepPcAsync() => throw new NotImplementedException();
    public static Task A28ExcludeKeepCloudAsync() => throw new NotImplementedException();
    public static Task A28RecreateInKeepScopeAsync() => throw new NotImplementedException();
    public static Task A28PreviewWithoutConfirmationAsync() => throw new NotImplementedException();
    public static Task A28ConfirmedRemovalPcAsync() => throw new NotImplementedException();
    public static Task A28ConfirmedRemovalCloudAsync() => throw new NotImplementedException();
    public static Task A28OnlyKeptSideEditedAsync() => throw new NotImplementedException();
    public static Task A28BothEditedKeepBothOnKeptSideAsync() => throw new NotImplementedException();
    public static Task A28NewOnRemovedSideAsync() => throw new NotImplementedException();
    public static Task A28ChangedOnRemovedSideAsync() => throw new NotImplementedException();
    public static Task A28SkippedChildAsync() => throw new NotImplementedException();
    public static Task A28OtherExclusionAsync() => throw new NotImplementedException();
    public static Task A28NestedRootAsync() => throw new NotImplementedException();
    public static Task A28CopyFailureAsync() => throw new NotImplementedException();
    public static Task A28FileFolderSwapAsync() => throw new NotImplementedException();
    public static Task A28RecreatedIdenticalAsync() => throw new NotImplementedException();
    public static Task A28RecreatedDifferentAsync() => throw new NotImplementedException();
    public static Task A28ListExclusionsAsync() => throw new NotImplementedException();
    public static Task A28IncludeQuietScopeAsync() => throw new NotImplementedException();
    public static Task A28CrashAfterRuleBeforeCopyAsync() => throw new NotImplementedException();
    public static Task A28CrashAfterRemovalBeforeSettlementAsync() => throw new NotImplementedException();
    public static Task A28CrashAfterIncludeAsync() => throw new NotImplementedException();
    public static Task A28SameScopeReissuedBeforeClearAsync() => throw new NotImplementedException();
    public static Task A28TwoDistinctOwedScopesAsync() => throw new NotImplementedException();
    public static Task A28PartialCompareKeepsDebtAsync() => throw new NotImplementedException();
    public static Task A29DotTildePrefixAsync() => throw new NotImplementedException();
    public static Task A29FileExtensionAsync() => throw new NotImplementedException();
    public static Task A29FileNameAsync() => throw new NotImplementedException();
    public static Task A29FolderNameAsync() => throw new NotImplementedException();
    public static Task A29UnignoreQuietScopeAsync() => throw new NotImplementedException();
    public static Task A29OtherMatchingRuleRemainsAsync() => throw new NotImplementedException();
    public static Task A29IgnoredChildTravelsWithFolderAsync() => throw new NotImplementedException();
    public static Task A32ValidRenameClosesNoticeAsync() => throw new NotImplementedException();
    public static Task A33DepartureBeforeArrivalAsync() => throw new NotImplementedException();
    public static Task A33EarlierPrerequisiteBeforeDepartureAsync() => throw new NotImplementedException();
    public static Task A33NoReciprocalWaitAsync() => throw new NotImplementedException();
    public static Task A34SameSizeSameSecondAsync() => throw new NotImplementedException();
    public static Task A34DifferentSizeSameSecondAsync() => throw new NotImplementedException();
    public static Task A34UntrustedTimeAsync() => throw new NotImplementedException();
    public static Task A34FileFolderNewestAsync() => throw new NotImplementedException();
    public static Task A34FileFolderPcWinsAsync() => throw new NotImplementedException();
    public static Task A34FileFolderDropboxWinsAsync() => throw new NotImplementedException();
    public static Task A35DifferentDeletedHashAsync() => throw new NotImplementedException();
    public static Task A35EarlierDeleteAsync() => throw new NotImplementedException();
    public static Task A35MissingTimeAsync() => throw new NotImplementedException();
    public static Task A35MalformedTimeAsync() => throw new NotImplementedException();
    public static Task A35MissingHashAsync() => throw new NotImplementedException();
    public static Task A35MalformedHashAsync() => throw new NotImplementedException();
    public static Task A35InsideOffsetUncertaintyAsync() => throw new NotImplementedException();
    public static Task A35LiveAtTargetAsync() => throw new NotImplementedException();
    public static Task A35LiveElsewhereAsync() => throw new NotImplementedException();
    public static Task A35RetryMatchingHistoricalDeleteAsync() => throw new NotImplementedException();
    public static Task A35RetryChangedHistoricalEvidenceAsync() => throw new NotImplementedException();
}
