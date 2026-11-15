using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void RunNewFileCaseRename() => NewFileCaseRenameAsync().GetAwaiter().GetResult();

    // No saved binding: the held first upload must resume at the same identity's fresh spelling.
    private static async Task NewFileCaseRenameAsync()
    {
        const string Original = "lower.txt";
        const string Renamed = "LOWER.txt";
        var attempt = Guid.NewGuid();
        using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
        TrustEchoSources(fixture);
        fixture.Attempts.Settles = (fixture.Inner, attempt);
        var local = LocalEntry(Original, LocalId);
        fixture.Local.Seed(local, [7]);
        var held = true;
        var opened = new List<string>();
        fixture.LocalView = new HookedSide(fixture.Local, beforeOpen: path =>
        {
            opened.Add(path);
            if (held) throw new ItemFailedException(path, TextKey.FailLocalInUse, null);
        });
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Original, null, local.Id, local, false, false));
        await DrainVanishedAsync(fixture, passes: 1);
        var notices = fixture.Inner.Issues(PairId);
        Check.Require(opened.Contains(Original) && notices is [{ Cause: TextKey.FailLocalInUse }] &&
            notices[0].Path == Original && fixture.Inner.ReadItemRetryState(PairId, [notices[0].Id])[notices[0].Id] is not null &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Original)) is null &&
            await fixture.Dropbox.ReadOneAsync(Original, default) is null,
            "C28 setup: the never-uploaded file is held with an old-spelling wait and no saved binding");
        var issueId = notices[0].Id;

        held = false;
        var fresh = await fixture.Local.MoveAsync(local, Renamed, default);
        Check.Require(fresh.Id == local.Id && fresh.Path == Renamed, "C28 setup: release precedes same-identity case rename");
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Renamed, Original, fresh.Id, fresh, false, false), 2);
        fixture.Inner.EndWorkPass(PairId);
        var refusals = await DrainVanishedAsync(fixture);
        var uploaded = await fixture.Dropbox.ReadOneAsync(Renamed, default);
        var bytes = new MemoryStream();
        if (uploaded is not null)
            await using (var stream = await fixture.Dropbox.OpenReadAsync(uploaded, null, default))
                await stream.CopyToAsync(bytes);
        var saved = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Renamed));
        Check.Require(refusals.Count == 0 && opened.Contains(Renamed) && uploaded?.Path == Renamed &&
            bytes.ToArray().SequenceEqual(new byte[] { 7 }) && saved?.LocalPath == Renamed && saved.DropboxPath == Renamed &&
            fixture.Inner.ReadItemRetryState(PairId, [issueId])[issueId] is null && fixture.Inner.Issues(PairId).Count == 0 &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0) &&
            fixture.Attempts.Completions > 0,
            $"C28: released new-file case rename uploads the fresh spelling and retires the old wait without a refusal loop; " +
            $"refusals={refusals.Count}, uploaded={uploaded?.Path}, completions={fixture.Attempts.Completions}");
    }
}
