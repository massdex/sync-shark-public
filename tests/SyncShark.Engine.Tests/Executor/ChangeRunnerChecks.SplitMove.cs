using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void RunSplitMoves() => RunSplitMovesAsync().GetAwaiter().GetResult();

    private static async Task RunSplitMovesAsync()
    {
        var failures = new List<Exception>();
        foreach (var mode in new[] { "ordinary", "flat", "nested", "mount", "reserved", "case", "occupied", "replacement" })
        {
            try { await SplitMoveAsync(mode); }
            catch (Exception error) { failures.Add(new Exception(mode, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task SplitMoveAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var target = mode == "case" ? "parent" : "Destination";
        fixture.Dropbox.MoveEntryLimit = mode == "ordinary" ? null : 1;
        var parent = mode == "nested" ? PathNames.Child(source.Path, "Nested") : source.Path;
        if (mode == "nested") fixture.Dropbox.Seed(FolderEntry(parent, "nested-id"), null);
        var first = FolderEntry(PathNames.Child(parent, "First"), "first-id");
        var second = FolderEntry(PathNames.Child(parent, "Second"), "second-id");
        if (mode == "mount") second = second with { SharedRoot = true };
        if (mode == "reserved") second = second with { Skip = nameof(TextKey.SkipReservedName) };
        fixture.Dropbox.Seed(first, null);
        fixture.Dropbox.Seed(second, null);
        if (mode == "occupied") fixture.Dropbox.Seed(FolderEntry(target, "occupant-id"), null);
        ItemFailedException? held = null;
        Entry? moved = null;
        try { moved = await new FolderMoveRunner(context).MoveAsync(Side.Dropbox, source, target, default); }
        catch (ItemFailedException error) { held = error; }
        catch (PathOccupiedException) when (mode == "occupied") { }
        var rows = fixture.Inner.FolderMoves(PairId, source.Id);
        if (mode is "ordinary" or "mount" or "reserved" or "case" or "occupied")
        {
            Check.Require(rows.Count == 0, "ordinary success and pre-arm refusals leave no split custody");
            Check.Require(mode switch
            {
                "ordinary" => moved?.Id == source.Id && moved.Path == target,
                "mount" or "reserved" => held is { Cause: TextKey.FailMoveSharedFolder, Move: null },
                "case" => held is { Cause: TextKey.FailDropboxTooManyFiles, Move: null },
                _ => held is null && fixture.Dropbox.FindById("occupant-id")?.Path == target
            }, "the lane preserves ordinary success, mount proof, case-only refusal and destination occupancy");
            return;
        }
        Check.Require(held is { Cause: TextKey.FailMoveInParts, Disposition: FailureDisposition.UnknownEffect } &&
            held.Move == new IssueSubject.UnknownMove(source.Id, source.Path, target, null, true) &&
            rows.Single() is { Phase: FolderMovePhase.Armed, DestinationId: null, Launch: null, JobId: null } &&
            await fixture.Dropbox.ReadOneAsync(target, default) is null,
            "the lane arms and hands off without creating or filling the destination");
        if (mode == "replacement")
        {
            fixture.Dropbox.Seed(source with { Id = "replacement-id" }, null);
            try
            {
                await new FolderMoveRunner(context).FinishAsync(rows, default);
                throw new Exception("a replacement source was accepted");
            }
            catch (ItemFailedException error)
            {
                Check.Require(error.Cause == TextKey.FailChanged && error.Move?.ExpectedId == source.Id &&
                    fixture.Dropbox.FindById(first.Id)?.Path == first.Path,
                    "a changed source identity refuses before listing or launching its children");
            }
            return;
        }
        moved = await new FolderMoveRunner(context).FinishAsync(rows, default);
        var filled = fixture.Inner.FolderMoves(PairId, source.Id);
        Check.Require(moved.Path == target && moved.Id != source.Id &&
            filled.Count == (mode == "nested" ? 2 : 1) && filled.All(move => move.Phase == FolderMovePhase.Filled &&
                move.Launch is null && move.JobId is null) &&
            fixture.Dropbox.FindById(first.Id)?.Path == PathNames.Child(mode == "nested" ? PathNames.Child(target, "Nested") : target, "First") &&
            fixture.Dropbox.FindById(second.Id)?.Path == PathNames.Child(mode == "nested" ? PathNames.Child(target, "Nested") : target, "Second") &&
            fixture.Dropbox.FindById(source.Id)?.Path == source.Path,
            "the fill preserves member identities, finishes nested custody, and leaves source cleanup to its owner");
    }
}
