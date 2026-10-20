using System.ComponentModel;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task QuarantineAsync(string root)
    {
        NativeIoDiagnosticChecks.Run();
        var crashedStage = AttendedElevatedAction.DownloadStagePath(@"C:\fake-crash\target.txt");
        SyncLocalEndpointStore.RequireDownloadStagePath(@"C:\fake-crash\target.txt", crashedStage);
        var stageExists = true;
        var checkpoint = new SyncDownloadStage(crashedStage, "stage-object", "", Verified: false);
        Require(!LocalWriter.DeleteMatchingStage(checkpoint, "replacement-object", () => stageExists = false) && stageExists,
            "deterministic crash stage replacement is never deleted");
        Require(LocalWriter.DeleteMatchingStage(checkpoint, "stage-object", () => stageExists = false) && !stageExists,
            "owned deterministic crash stage cleanup frees the exact retry name (fake handle)");
        using (var artifactStore = SqliteStateStore.Open(Path.Combine(root, "case-artifact"), Settings, TimeProvider.System))
        {
            var artifactFx = Build(suppliedStore: artifactStore);
            var stage = ".SyncShark-Transfer-" + new string('a', 64) + ".PARTIAL";
            artifactFx.B.Seed(stage, "untrusted shared bytes");
            artifactFx.B.Seed("safe.txt", "ordinary bytes");
            await FirstSyncAsync(artifactFx.Run(), artifactFx);
            Require(await artifactFx.A.ObserveAsync(stage, default) is null &&
                await artifactFx.A.ObserveAsync("safe.txt", default) is not null,
                "mixed-case transfer artifact is never materialized from cloud while sibling syncs");
            Require(LocalItems.IsArtifactPath("folder/" + stage) && SyncLocalTransfer.IsTransferArtifactName(stage),
                "local artifact filtering uses Windows case identity");
        }
        await QuarantineHistoryAsync(root);
        await QuarantineCheckpointAsync(root);
        await QuarantineChoicesAsync(root);
        await QuarantineGrantGuardAsync(root);
        await QuarantineNativeStageAsync(root);
        await QuarantineScanReplacementAsync(root);
        await QuarantineRenameAsync(root);
        Require(!QuarantineFailure.IsQuarantine(new FileNotFoundException()) &&
            !QuarantineFailure.IsQuarantine(new Win32Exception(5)) &&
            !QuarantineFailure.IsQuarantine(new IOException("identity mismatch")), "missing/ACL/identity errors are not quarantine");
        foreach (var code in new[] { 225, 226 })
        foreach (var mode in new[] { "open", "read", "write" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, code + mode), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var source = mode == "write" ? fx.B : fx.A;
            source.Seed("blocked.txt", "blocked bytes");
            source.Seed("sibling.txt", "ordinary bytes");
            var fault = new QuarantineWriter(mode == "write" ? fx.A : source, mode, code);
            fx.Provider.AddWriter(fx.Pair.A, fault);
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            Require(store.FirstSyncDone("p1"), $"{mode}/{code}: held item does not block first sync");
            Require((await (mode == "write" ? fx.A : fx.B).ObserveAsync("sibling.txt", default)) is not null,
                $"{mode}/{code}: independent sibling completed");
            var heldRoot = mode == "write" ? "rb" : "ra";
            Require(store.Live(heldRoot, ["blocked.txt"]).Single().Flags.HasFlag(ItemFlags.Quarantined), "hold lives on entity row");
            Require(store.UnresolvedIntents("p1").Count == 0, "settled quarantine has no automatic retry intent");
            var notice = store.OpenNotices("p1").Single(n => n.Quarantine is not null);
            Require(notice.Detail.Contains($"Win32={code}") && NoticeChoices.Options(notice).Count == 5,
                "native diagnostics and explicit actions survive notice serialization");
            var calls = fault.Attempts;
            await run.IncrementalAsync("p1", new RecordingProgress(), default);
            Require(fault.Attempts == calls, "ordinary run never retries held file");
            var gen = store.BeginGeneration(heldRoot, "test-absent");
            store.WriteSnapshot(new Snapshot(heldRoot, mode == "write" ? Side.B : Side.A, gen, "", [], []));
            store.WriteDelta(new Delta(heldRoot, mode == "write" ? Side.B : Side.A, gen, "", "",
                [new Change(ChangeKind.Delete, "", "blocked.txt", null, DateTimeOffset.UtcNow)], false, []));
            Require(store.Live(heldRoot, ["blocked.txt"]).Count == 1, "subsequent absence never erases held entity");
            var retry = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineRetry, default);
            Require(retry.Outcome != Outcome.Done && store.Live(heldRoot, ["blocked.txt"]).Single().Flags.HasFlag(ItemFlags.Quarantined),
                "explicit failed retry leaves item held");
            fault.Enabled = false;
            retry = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineRetry, default);
            Require(retry.Outcome == Outcome.Done && !store.Live(heldRoot, ["blocked.txt"]).Single().Flags.HasFlag(ItemFlags.Quarantined),
                "successful explicit retry clears exact hold: " + retry.Outcome + " " + retry.Reason);
            Console.WriteLine($"PASS quarantine {mode} Win32 {code}: sibling/first-sync/persistence/retry");
        }
        using (var store = SqliteStateStore.Open(Path.Combine(root, "choices"), Settings, TimeProvider.System))
        {
            var fx = Build(suppliedStore: store);
            fx.B.Seed("blocked.txt", "bytes");
            var fault = new QuarantineWriter(fx.A, "write", 225);
            fx.Provider.AddWriter(fx.Pair.A, fault);
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var notice = store.OpenNotices("p1").Single(n => n.Quarantine is not null);
            var replacementFolder = new SyncAction("check-possible-grant", 30, ActionKind.MkDir, Side.A, "blocked.txt", null, null, null, false, null);
            var denied = new FakeElevation(() => Require(store.DefenderGrantConflict("p1", replacementFolder) is { Verified: false },
                "possible exclusion intent is durable before fake UAC starts"));
            var result = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderExclude, default, denied);
            Require(result.Outcome == Outcome.Skipped && denied.Calls == 1 && fault.Attempts == 1, "UAC decline does not retry");
            Require(store.DefenderGrantConflict("p1", replacementFolder) is { Verified: false }, "failed attended call retains possible grant independently of notice");
            Require(store.NoticeById("p1", notice.NoticeId!)!.Value.Notice.Detail.Contains("Exclusion did not complete"), "UAC failure reason remains in durable notice");
            fx.B.Seed("blocked.txt", "newer cloud revision");
            result = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineDeleteDropbox, default);
            Require(result.Outcome == Outcome.Skipped && await fx.B.ObserveAsync("blocked.txt", default) is not null,
                "Dropbox delete is bound to reviewed object and revision");
            result = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineIgnore, default);
            Require(result.Outcome == Outcome.Done && store.Live("rb", ["blocked.txt"]).Single().Flags.HasFlag(ItemFlags.Quarantined), "ignore keeps hold");
            store.AddNotices([notice]);
            store.AddNotices([notice with { Quarantine = null }]);
            Require(!store.OpenNotices("p1").Any(n => n.ReasonKey == QuarantineFailure.NoticeKey), "late duplicate detection does not undo ignore");
        }
        foreach (var invalid in new[] { "../outside", "C:\\outside", "a*", "x:stream", "a/../b" })
        {
            try { AttendedElevatedAction.ValidatePath("C:\\safe", invalid); throw new Exception("unsafe path accepted: " + invalid); }
            catch (ArgumentException) { }
        }
        foreach (var name in new[] { "it's;$(whoami).txt", "\u2018\u2019\u201a\u201b\u201c\u201d\u201e\u201f; & ` $(echo INJECTED).txt" })
        {
            var literal = AttendedElevatedAction.ValidatePath(@"C:\safe", name);
            var command = AttendedElevatedAction.Command(AttendedElevatedAction.DefenderExclude, [@"C:\safe", literal]);
            Require(!command.Contains(name) && command.Contains(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(literal))),
                "untrusted path enters elevated fixed script only as UTF16 base64 data");
            Require(command.Contains("$p.StartsWith($boundary") && command.Contains("$root.Contains('%')"), "elevated script rechecks root boundary and expansions");
            Require(Path.GetDirectoryName(AttendedElevatedAction.DownloadStagePath(literal)) == @"C:\safe", "deterministic stage remains exact sibling");
        }
        foreach (var (unsafeRoot, relative) in new[] { (@"C:\%TEMP%", "ordinary.txt"), (@"C:\safe", "%TEMP%.txt") })
        {
            try { AttendedElevatedAction.ValidatePath(unsafeRoot, relative); throw new Exception("percent expansion accepted"); }
            catch (ArgumentException) { }
        }
        Console.WriteLine("PASS quarantine notice choices, fake elevation denial, revision guard, literal path validation");
    }

    private static async Task QuarantineNativeStageAsync(string parent)
    {
        var root = Path.Combine(parent, "quarantine-stage-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Native fixture must be fresh.");
        Directory.CreateDirectory(root);
        Console.WriteLine("Native owned-stage fixture (retained): " + root);
        var target = Path.Combine(root, "target.txt");
        var path = AttendedElevatedAction.DownloadStagePath(target);
        string identity;
        await using (var file = SyncLocalFileAuthority.CreateNewMovableFile(path))
        {
            await file.CreateGuardedWriteStream().WriteAsync(new byte[] { 1, 2, 3 });
            await file.Stream.FlushAsync();
            var native = file.ReadIdentity();
            identity = SyncLocalEndpointStore.ObjectId(native.VolumeSerialNumber, native.FileId);
        }
        var checkpoint = new SyncDownloadStage(path, identity, "", Verified: false);
        Require(LocalWriter.TryDeleteOwnedStage(checkpoint) && !File.Exists(path),
            "native owned deterministic crash stage is removed by production cleanup");
        await File.WriteAllTextAsync(path, "new unrelated occupant");
        Require(!LocalWriter.TryDeleteOwnedStage(checkpoint) && File.Exists(path) && await File.ReadAllTextAsync(path) == "new unrelated occupant",
            "native replacement at deterministic stage name is preserved");
        Require(!File.Exists(target), "native stage cleanup never materializes target");
    }

    private sealed class FakeElevation(Action? beforeFailure = null) : IElevatedAction
    {
        internal int Calls;
        public Task<int> RunAsync(string verb, IReadOnlyList<string> args, CancellationToken cancellationToken)
        { Calls++; beforeFailure?.Invoke(); throw new Win32Exception(1223); }
    }

    private class QuarantineWriter(IEndpointWriter inner, string mode, int code) : IEndpointWriter
    {
        internal bool Enabled = true;
        internal bool Checkpoint;
        internal HashSet<string>? AllowedPaths;
        internal string? TargetRoot;
        internal int Attempts;
        private void Fail(string path, string operation)
        {
            if (!Enabled || path != "blocked.txt" || mode != operation) return;
            Attempts++;
            throw new IOException("injected I/O", new Win32Exception(code));
        }
        public async Task<ItemState> PutAsync(SyncAction action, Stream stream, CancellationToken ct)
        {
            if (Checkpoint && action.Path == "blocked.txt") await ProviderEffectContext.Current.Value!.RecordAsync(SyncDirectDownloadCheckpoint.Kind, "fake-checkpoint");
            if (AllowedPaths is not null)
            {
                var final = AttendedElevatedAction.ValidatePath(TargetRoot!, action.Path);
                var stage = action.QuarantineStage ? AttendedElevatedAction.DownloadStagePath(final) : final + ".random.partial";
                if (!AllowedPaths.Contains(final) || !AllowedPaths.Contains(stage))
                    throw new IOException("fake antivirus blocked non-excluded staged write", new Win32Exception(code));
            }
            else Fail(action.Path, "write");
            return await inner.PutAsync(action, stream, ct);
        }
        public async Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct)
        {
            Fail(item.Path, "open");
            if (Enabled && item.Path == "blocked.txt" && mode == "read") { Attempts++; return new ThrowingRead(code); }
            return await inner.OpenReadAsync(item, ct);
        }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState item, string id, CancellationToken ct) => inner.PreserveAsync(item, id, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
    }
    private sealed class ThrowingRead(int code) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(new IOException("read failed", unchecked((int)(0x80070000u | (uint)code))));
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("read failed", unchecked((int)(0x80070000u | (uint)code))));
    }
}
