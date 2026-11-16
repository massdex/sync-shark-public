using System.Net;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static async Task SplitListingReservedMountAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var reservedPath = PathNames.Child(source.Path, "CON");
        var mountedPath = PathNames.Child(reservedPath, "Mounted");
        fixture.Dropbox.Seed(FolderEntry(reservedPath, "reserved-id"), null);
        fixture.Dropbox.Seed(FolderEntry(mountedPath, "mounted-id") with { SharedRoot = true }, null);
        fixture.Dropbox.MoveEntryLimit = 0;
        var mounted = DropboxListingChecks.Folder(fixture.Pair.DropboxRoot, mountedPath, "mounted-id");
        mounted[DropboxProtocol.SharingInfo] = new Dictionary<string, object>
        {
            [DropboxProtocol.SharedFolderId] = "shared-folder"
        };
        HttpResponseMessage Page() => DropboxListingChecks.Page(false, "end",
            mounted, DropboxListingChecks.Folder(fixture.Pair.DropboxRoot, reservedPath, "reserved-id"));
        using var listing = new DropboxListingChecks.Fixture(fixture.Pair, _ => Page(), _ => Page());
        fixture.Dropbox.Listing = (under, options, ct) => listing.Listing.ReadAll(under, options,
            DropboxListingChecks.BeginListing(), ct);
        var observed = new List<Listed>();
        await foreach (var item in fixture.Dropbox.ReadAllAsync(source.Path, new ListOptions(true, true), default))
            observed.Add(item);
        Check.Require(observed.Count == 1 && observed[0].Path == reservedPath &&
            observed[0].Entry?.Skip == nameof(TextKey.SkipReservedName),
            "the real listing suppresses a mounted descendant even when its metadata precedes the reserved ancestor");
        var writes = fixture.Hooks.Count(nameof(IStateStore.SetFolderMove));
        try
        {
            await new FolderMoveRunner(context).MoveAsync(Side.Dropbox, source, "Destination", default);
            throw new Exception("the hidden mounted subtree was admitted for splitting");
        }
        catch (ItemFailedException error) when (error.Cause == TextKey.FailMoveSharedFolder) { }
        Check.Require(listing.Handler.Requests.Count == 2 && fixture.Dropbox.ExclusiveCreates == 0 &&
            fixture.Dropbox.MoveBatchCalls == 0 && fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 &&
            fixture.Hooks.Count(nameof(IStateStore.SetFolderMove)) == writes &&
            await fixture.Dropbox.ReadOneAsync(source.Path, default) == source &&
            await fixture.Dropbox.ReadOneAsync("Destination", default) is null &&
            fixture.Dropbox.FindById("mounted-id")?.Path == mountedPath,
            "the reserved ancestor refuses the ordinary oversized move before custody or provider writes");
    }

    private static async Task SplitListingMalformedAsync(bool filling)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var source = row.Dropbox!;
        var local = FolderEntry("Destination", row.Synced!.LocalId);
        fixture.Local.Seed(local, null);
        var child = RemoteEntry(PathNames.Child(source.Path, "Child"), "child-id");
        fixture.Dropbox.Seed(child, [1]);
        fixture.Dropbox.MoveEntryLimit = 0;
        if (filling)
        {
            fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(source.Id, source.Id, null,
                source.Path, local.Path, null, FolderMovePhase.Armed, null, null, 0));
            SeedSplitHold(fixture, source, local);
        }
        else
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, local.Path, source.Path,
                local.Id, local, false, false), page: 2);
        var independent = RemoteEntry("Independent", "independent-id");
        fixture.Dropbox.Seed(independent, [1]);
        SeedRemote(fixture, independent);
        var requests = new List<int>();
        async IAsyncEnumerable<Listed> ReadListing(string under, ListOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Check.Require(under == source.Path && options.Recursive == !filling,
                "the real listing is reached at the proof or fill boundary, not during unrelated work");
            using var listing = new DropboxListingChecks.Fixture(fixture.Pair,
                _ => DropboxListingChecks.Page(true, "next", DropboxListingChecks.File(fixture.Pair.DropboxRoot,
                    child.Path, id: child.Id)),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") });
            try
            {
                await foreach (var item in listing.Listing.ReadAll(under, options,
                    DropboxListingChecks.BeginListing(), ct)) yield return item;
            }
            finally { requests.Add(listing.Handler.Requests.Count); }
        }
        fixture.Dropbox.Listing = ReadListing;
        var writes = fixture.Hooks.Count(nameof(IStateStore.SetFolderMove));
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), events,
            configure: live => live with { Local = context.Local, Dropbox = context.Dropbox })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var issues = fixture.Inner.Issues(PairId);
        // Unrelated retirement repeats the proof once; failure alone does not repeat. A kept fill ends its root.
        Check.Require(requests.Count == (filling ? 1 : 2) && requests.All(count => count == 2) &&
            fixture.Inner.SyncedById(PairId, Side.Dropbox, independent.Id) is not null &&
            events.All(value => value.Kind != RunEventKind.Failed) && issues.Count == 1 &&
            issues[0].Kind == IssueKind.Failure && IssuePolicy.OwnsItem(issues[0], Side.Local, local.Id) &&
            IssuePolicy.OwnsItem(issues[0], Side.Dropbox, source.Id) &&
            fixture.Hooks.Count(nameof(IStateStore.SetFolderMove)) - writes == (filling ? 1 : 0) &&
            fixture.Dropbox.MoveBatchCalls == 0 && fixture.Dropbox.FindById(child.Id)?.Path == child.Path,
            "a malformed continuation page stays item-scoped while unrelated work completes without background failure");
        if (filling)
            Check.Require(issues[0].Cause == TextKey.FailMoveInParts &&
                fixture.Inner.MoveResponsibility(PairId, source.Id)?.Id == issues[0].Id &&
                fixture.Dropbox.ExclusiveCreates == 1 &&
                fixture.Inner.FolderMoves(PairId, source.Id).Single() is
                    { Phase: FolderMovePhase.Filling, Launch: null, JobId: null, DestinationId: not null },
                "a failed partial fill keeps interrupted-move custody and never treats the listing as empty or Filled");
        else
            Check.Require(issues[0].Cause == TextKey.ReadDropboxFailed && issues[0].Path == local.Path &&
                events.Count(value => value.Kind == RunEventKind.Issue && value.Cause == TextKey.ReadDropboxFailed &&
                    value.Path == local.Path) == 1 && fixture.Dropbox.ExclusiveCreates == 0 &&
                fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 &&
                await fixture.Dropbox.ReadOneAsync(local.Path, guard.Token) is null,
                "a failed proof produces exactly one scoped rename notice before arming or writing the destination");
    }
}
