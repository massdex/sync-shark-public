using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task RunSlowDeleteChecksAsync(string root, string selected)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh bounded test directory.");
        Directory.CreateDirectory(root);
        _root = root;
        if (selected is "all" or "concurrency") await ConditionalDeleteJobsOverlapAsync();
        if (selected is "all" or "window") await FolderRecoveryWindowFrozenAsync();
        if (selected is "all" or "settled") await SettledRecoveryIsNotReplayedAsync();
        if (selected is "all" or "grace") await PreviouslyDeletedVictimSettlesAsync();
        if (selected is "all" or "eta") KindEtasStaySeparate();
        if (selected is "all" or "adaptive") await DeleteAdmissionAdaptsAsync();
        await RunSlowDeleteReviewChecksAsync(selected);
        if (selected == "existing")
        {
            await DeleteBatchRaceAsync();
            await FolderTreeChecksAsync();
        }
    }

    private static async Task ConditionalDeleteJobsOverlapAsync()
    {
        var uniqueBinding = Binding() with { ConnectionId = Guid.NewGuid().ToString() };
        using var h = new Harness(uniqueBinding);
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { DeleteBatchSize = 1, BatchPollInterval = TimeSpan.FromMilliseconds(1) });
        var firstCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        h.Wire.DeleteBatchAsyncJob = true;
        h.Wire.BeforeDeleteJobCheck = async () =>
        {
            if (Interlocked.Increment(ref checks) == 1) firstCheck.TrySetResult();
            else secondCheck.TrySetResult();
            await release.Task;
        };
        var tasks = new List<Task>();
        try
        {
            h.Wire.SeedFile("overlap-a.txt", "a");
            h.Wire.SeedFile("overlap-b.txt", "b");
            var a = (await writer.ObserveAsync("overlap-a.txt", CancellationToken.None))!;
            var b = (await writer.ObserveAsync("overlap-b.txt", CancellationToken.None))!;
            tasks.Add(AuthorizedFixtureDeleteAsync(writer, DeleteAction(a.Path, a), CancellationToken.None));
            await firstCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tasks.Add(AuthorizedFixtureDeleteAsync(writer, DeleteAction(b.Path, b), CancellationToken.None));
            await secondCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(tasks.All(task => !task.IsCompleted), "two conditional jobs poll concurrently before either completes");
            var binding = uniqueBinding.Normalize();
            Require(DropboxUploadCommitGate.For(binding.ConnectionId, binding.RootNamespaceId, 1).CurrentCount == 1,
                "polling does not hold the namespace write-submission budget");
        }
        finally { release.TrySetResult(); await Task.WhenAll(tasks); }
    }

    private static async Task FolderRecoveryWindowFrozenAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/late.txt", "late");
        var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big")) { RevalidateDelete = () => Task.FromResult(true) };
        string durable = "";
        rig.H.Wire.AfterBuriedRevisionRead = () => throw new InvalidOperationException("temporary recovery read failure");
        var initial = await rig.B.DeleteFolderTreesAsync(rig.Cursor, [tree], (_, state) =>
        { durable = state; return Task.CompletedTask; }, CancellationToken.None);
        Require(initial.Single().Status == FolderTreeStatus.Uncertain, "fixture leaves a retryable restore");
        // After the job window closes, this feed must never be read again, even if it reports user changes.
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        rig.H.Wire.AfterBuriedRevisionRead = null;
        var resumed = await rig.B.ReconcileFolderTreesAsync(rig.Cursor, [tree], durable, state =>
        { durable = state; return Task.CompletedTask; }, CancellationToken.None);
        Require(resumed.Single().Status == FolderTreeStatus.Completed && rig.H.Wire.Restores.Count == 1,
            "reconcile uses the durable job window, not a growing middle-cursor feed");
        rig.H.Wire.MoveOutsideRoot("big/late.txt", "/Elsewhere/late.txt");
        resumed = await rig.B.ReconcileFolderTreesAsync(rig.Cursor, [tree], durable,
            _ => Task.CompletedTask, CancellationToken.None);
        Require(resumed.Single().Status == FolderTreeStatus.Completed && rig.H.Wire.Restores.Count == 1,
            "completed restoration is not replayed over a later user removal");
    }

    private static async Task SettledRecoveryIsNotReplayedAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/late.txt", "late");
        rig.H.Wire.RestoreDifferentRevision = true;
        var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big")) { RevalidateDelete = () => Task.FromResult(true) };
        string durable = "";
        var initial = await rig.B.DeleteFolderTreesAsync(rig.Cursor, [tree], (_, state) =>
        { durable = state; return Task.CompletedTask; }, CancellationToken.None);
        Require(initial.Single().Unrestored.Count == 1 && rig.H.Wire.Restores.Count == 1,
            "a differing restored revision is terminal and reported for inspection");
        rig.H.Wire.MoveOutsideRoot("big/late.txt", "/Elsewhere/late.txt");
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        var resumed = await rig.B.ReconcileFolderTreesAsync(rig.Cursor, [tree], durable,
            _ => Task.CompletedTask, CancellationToken.None);
        Require(resumed.Single().Status == FolderTreeStatus.Completed &&
            resumed.Single().Unrestored.SequenceEqual(initial.Single().Unrestored) && rig.H.Wire.Restores.Count == 1,
            "a terminal recovery notice is retained without replay after a later user removal");
    }

    private static async Task PreviouslyDeletedVictimSettlesAsync()
    {
        using var rig = await TreeRigAsync(60);
        var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big")) { RevalidateDelete = () => Task.FromResult(true) };
        var victims = tree.Victims.Append(tree.Folder).ToArray();
        var earlier = victims[0];
        rig.H.Wire.SeedChangePage("no-job-middle", victims.Skip(1)
            .Select(item => rig.H.Wire.ChangeDeleted("/Pilot/" + item.Path)).ToArray(), false, "no-job-end");
        var recovery = JsonSerializer.Serialize(new
        {
            Before = new[] { new DropboxChange(earlier.Path, null) }, Middle = "no-job-middle",
            SubmittedAt = DateTimeOffset.UtcNow
        });
        var result = await rig.B.ReconcileFolderTreesAsync(rig.Cursor, [tree], recovery,
            _ => Task.CompletedTask, CancellationToken.None);
        Require(result.Single().Status == FolderTreeStatus.Completed && result.Single().Unconfirmed.Count == 0,
            "a pre-submit tombstone plus job-window tombstones settles without two-hour grace");
    }

    private sealed class EtaClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class EtaSink : IProgress<RunProgress>
    {
        public List<RunProgress> Values { get; } = [];
        public void Report(RunProgress value) => Values.Add(value);
    }
    private static void KindEtasStaySeparate()
    {
        var clock = new EtaClock();
        var sink = new EtaSink();
        var progress = new ProgressThrottle(sink, clock, TimeSpan.Zero);
        progress.Report("Applying", null, 0, 110, kinds:
            [new(ActionKind.CopyNew, 0, 100), new(ActionKind.Delete, 0, 10)]);
        clock.Now += TimeSpan.FromSeconds(10);
        progress.Report("Applying", null, 90, 110, kinds:
            [new(ActionKind.CopyNew, 90, 100), new(ActionKind.Delete, 0, 10)]);
        Require(sink.Values.Last().Message!.Contains("Delete 0/10, ETA awaiting sample", StringComparison.Ordinal),
            "upload completions do not manufacture a delete ETA");
        clock.Now += TimeSpan.FromSeconds(10);
        progress.Report("Applying", null, 102, 110, kinds:
            [new(ActionKind.CopyNew, 100, 100), new(ActionKind.Delete, 2, 10)]);
        Require(sink.Values.Last().Message!.Contains("Delete 2/10, 0.1/s, ETA 1m 20s", StringComparison.Ordinal),
            "delete ETA uses delete completions, not the aggregate upload rate");
    }

    private static async Task DeleteAdmissionAdaptsAsync()
    {
        var gate = DropboxDeleteJobGate.For(Guid.NewGuid().ToString(), "namespace");
        await gate.EnterAsync();
        await gate.EnterAsync();
        var third = gate.EnterAsync();
        Require(!third.IsCompleted, "two outstanding jobs are the initial namespace bound");
        gate.Throttled();
        gate.Exit(false);
        await Task.WhenAny(third, Task.Delay(100));
        Require(!third.IsCompleted, "write throttling cuts admission to one outstanding job");
        gate.Exit(false);
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Exit(true);
        for (var i = 0; i < 3; i++) { await gate.EnterAsync(); gate.Exit(true); }
        await gate.EnterAsync();
        var recovered = gate.EnterAsync();
        await recovered.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Exit(false); gate.Exit(false);
        for (var i = 0; i < 12; i++) { await gate.EnterAsync(); gate.Exit(true); }
        for (var i = 0; i < 4; i++) await gate.EnterAsync();
        var fifth = gate.EnterAsync();
        Require(!fifth.IsCompleted, "success recovery never exceeds four namespace jobs");
        gate.Exit(false);
        await fifth.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 4; i++) gate.Exit(false);
    }
}
