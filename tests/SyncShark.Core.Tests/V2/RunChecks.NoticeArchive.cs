using System.Diagnostics;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task NoticeArchiveCrashAsync(string directory)
    {
        var root = Path.Combine(directory, "local");
        Directory.CreateDirectory(root);
        LocalChecks.Seed(root, "file.txt", "retained local bytes");
        var writer = await LocalWriter.OpenAsync(LocalChecks.RootOf(root, "ra"), Side.A, "pair00001",
            LocalChecks.InstallationId, Path.Combine(directory, "recovery"), CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage);
        var loser = (await writer.ObserveAsync("file.txt", CancellationToken.None))!;
        await File.WriteAllTextAsync(Path.Combine(directory, "loser.json"), JsonSerializer.Serialize(loser));
        SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = path =>
        {
            File.WriteAllText(Path.Combine(directory, "archive-path.txt"), path);
            Environment.Exit(73); // Actual crash boundary: no finally/Dispose restores the access envelope.
        };
        await writer.PreserveAsync(loser, "original", CancellationToken.None);
        throw new InvalidOperationException("Archive crash hook was not reached.");
    }

    public static async Task InterruptedNoticeArchiveAsync()
    {
        var directory = Path.GetFullPath(LocalChecks.FreshRoot("interrupted-notice-archive"));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(RunChecks).Assembly.Location);
        start.ArgumentList.Add("--v2-notice-archive-crash");
        start.ArgumentList.Add(directory);
        using var child = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await child.WaitForExitAsync(deadline.Token);
        Require(child.ExitCode == 73, "real Recovery interruption exits at the bound archive hook");
        var loser = JsonSerializer.Deserialize<ItemState>(await File.ReadAllTextAsync(Path.Combine(directory, "loser.json")))!;
        var archive = await File.ReadAllTextAsync(Path.Combine(directory, "archive-path.txt"));
        Require(File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(archive)) ||
            File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(archive + ".part")),
            "crashed archive retains its actual access-repair envelope");
        var root = Path.Combine(directory, "local");
        var localRoot = LocalChecks.RootOf(root, "ra");
        var writer = await LocalWriter.OpenAsync(localRoot, Side.A, "pair00001", LocalChecks.InstallationId,
            Path.Combine(directory, "recovery"), CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage);
        LocalChecks.Seed(root, "file.txt", "winner bytes");
        var winner = (await writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var other = new FakeEndpoint(Side.B, "rb") { DropboxHashes = true };
        var otherWinner = other.Seed("file.txt", "winner bytes");
        using var store = SqliteStateStore.Open(Path.Combine(directory, "state"), Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("pair00001", "restore", localRoot,
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);
        store.SavePair(pair);
        var original = new SyncAction("original", 4, ActionKind.Update, Side.A, "file.txt", null,
            otherWinner, loser, true, "key");
        store.AddNotices([new Notice(pair.PairId, 4, NoticeKind.AutoConflict, "file.txt", "newest", "auto-conflict",
            RetainedVersion: new RetainedNoticeVersion(original.ActionId, Side.A, loser, winner))]);
        var noticeId = store.OpenNotices(pair.PairId).Single().NoticeId!;
        store.CommitResult(pair.PairId, store.BeginGeneration(pair.A.RootId, "run"), original,
            new ActionResult(original.ActionId, Outcome.Done, winner, otherWinner,
                new RecoveryRef(Side.A, archive, null, loser.Hash!, loser.Size), null));
        var provider = new FakeEndpointProvider().Add(pair.B, other).AddWriter(pair.A, writer);
        var run = new SyncRun(store, NewExecutor(store), provider, Settings, TimeProvider.System);
        var result = await run.RestoreNoticeVersionAsync(pair.PairId, noticeId, false, CancellationToken.None);
        Require(result.Outcome == Outcome.Done && await File.ReadAllTextAsync(Path.Combine(root, "file.txt")) == "retained local bytes",
            "notice restore repairs interrupted Recovery access and restores the exact local loser: " + result.Reason);
        Require(store.OpenNotices(pair.PairId).Single().RetainedVersion?.Loser.Hash == winner.Hash &&
            !File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(archive)) &&
            !File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(archive + ".part")),
            "repair consumes the interrupted envelope and the displaced winner remains recoverable");
    }
}
