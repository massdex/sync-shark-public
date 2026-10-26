using System.Diagnostics;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static class LocalRecoveryInterruptionChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("Recovery interruption checks require a fresh root.");
        Directory.CreateDirectory(root);
        foreach (var phase in new[] { "copy", "promoted", "only-copy", "second-kill" })
        {
            var fixture = Path.Combine(root, phase);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(LocalRecoveryInterruptionChecks).Assembly.Location);
            start.ArgumentList.Add("--v2-recovery-kill-child");
            start.ArgumentList.Add(fixture);
            start.ArgumentList.Add(phase);
            using var child = Process.Start(start) ?? throw new IOException("Recovery child did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                throw;
            }
            LocalChecks.Require(child.ExitCode == 73, "Recovery child exited without disposing at " + phase + ": " + await stdout + await stderr);
            if (phase == "second-kill")
            {
                using var second = Process.Start(start) ?? throw new IOException("Second Recovery child did not start.");
                var secondOut = second.StandardOutput.ReadToEndAsync();
                var secondError = second.StandardError.ReadToEndAsync();
                try { await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch
                {
                    if (!second.HasExited) { second.Kill(); await second.WaitForExitAsync(); }
                    throw;
                }
                LocalChecks.Require(second.ExitCode == 73, "second cleanup interruption: " + await secondOut + await secondError);
            }
            var local = Path.Combine(fixture, "local");
            var recovery = Path.Combine(fixture, "recovery");
            var victim = JsonSerializer.Deserialize<ItemState>(await File.ReadAllTextAsync(Path.Combine(fixture, "victim.json")))!;
            var writer = await LocalChecks.OpenWriter(local, recovery, CancellationToken.None);
            var final = Path.Combine(recovery, ".syncshark-recovery-" + LocalChecks.PairId,
                LocalChecks.OperationId("interrupted-action") + ".recovery");
            LocalChecks.Require(File.Exists(final) == (phase is "promoted" or "only-copy") &&
                File.Exists(final + ".part") == (phase == "copy"),
                "kill exposes only an incomplete private stage or the promoted Recovery, never a partial final");
            if (phase == "only-copy")
                LocalChecks.Require(!File.Exists(Path.Combine(local, "file.bin")) &&
                    File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(final + ".part")),
                    "only-copy kill follows actual source disposal while protected Recovery remains open");
            else
                LocalChecks.Require(File.ReadAllBytes(Path.Combine(local, "file.bin")).SequenceEqual(Bytes()),
                    "kill during Recovery retains the complete original at " + phase);
            if (phase == "second-kill")
                LocalChecks.Require(File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(final + ".part")),
                    "second kill retains the encrypted envelope after actual stage deletion");
            var reference = await writer.PreserveAsync(victim, "interrupted-action", CancellationToken.None);
            LocalChecks.Require(!File.Exists(Path.Combine(local, "file.bin")) &&
                File.ReadAllBytes(reference.Location).SequenceEqual(Bytes()), "Recovery rerun completes exact archive at " + phase);
            LocalChecks.Require(!Directory.EnumerateFiles(recovery, "*.part*", SearchOption.AllDirectories).Any(),
                "Recovery rerun consumes its staging file and ACL envelope at " + phase);
        }
        await UnboundStageAsync(Path.Combine(root, "unbound"));
        await MismatchedStageAsync(Path.Combine(root, "mismatched"));
        await EnvelopeFailureKindsAsync(Path.Combine(root, "envelope-errors"));
        await DownloadOutageAsync(Path.Combine(root, "download"));
    }

    internal static async Task CrashAsync(string root, string phase)
    {
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var recoveryRoot = Path.Combine(root, "recovery");
        var resuming = File.Exists(Path.Combine(root, "victim.json"));
        var writer = await LocalChecks.OpenWriter(local, recoveryRoot, CancellationToken.None);
        ItemState victim;
        if (resuming)
            victim = JsonSerializer.Deserialize<ItemState>(await File.ReadAllTextAsync(Path.Combine(root, "victim.json")))!;
        else
        {
            await File.WriteAllBytesAsync(Path.Combine(local, "file.bin"), Bytes());
            victim = (await writer.ObserveAsync("file.bin", CancellationToken.None))!;
            await File.WriteAllTextAsync(Path.Combine(root, "victim.json"), JsonSerializer.Serialize(victim));
        }
        if (phase is "copy" or "mismatched" || phase == "second-kill" && !resuming)
            SyncLocalRecovery.RecoveryCopyIntervalForTest = _ => Environment.Exit(73);
        else if (phase == "second-kill")
            SyncLocalFileAuthority.InterruptedStageRemovedForTest = path =>
            {
                LocalChecks.Require(!File.Exists(path), "second interruption really follows stage deletion");
                Environment.Exit(73);
            };
        else if (phase == "promoted") SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = () => Environment.Exit(73);
        else if (phase == "only-copy") SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = _ =>
        {
            LocalChecks.Require(!File.Exists(Path.Combine(local, "file.bin")), "only-copy source is actually absent");
            Environment.Exit(73);
        };
        else throw new ArgumentException("Unknown Recovery interruption phase.");
        await writer.PreserveAsync(victim, "interrupted-action", CancellationToken.None);
        throw new InvalidOperationException("The Recovery interruption seam was not reached.");
    }

    private static async Task UnboundStageAsync(string root)
    {
        var local = Path.Combine(root, "local");
        var recovery = Path.Combine(root, "recovery");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "file.txt"), "original");
        var writer = await LocalChecks.OpenWriter(local, recovery, CancellationToken.None);
        var victim = (await writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var stage = Path.Combine(recovery, ".syncshark-recovery-" + LocalChecks.PairId,
            LocalChecks.OperationId("unbound-action") + ".recovery.part");
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        await File.WriteAllTextAsync(stage, "unbound bytes");
        var notices = new List<SyncByteProgress>();
        using var progress = SyncByteProgressStream.Begin("file.txt", item =>
        { notices.Add(item); return Task.CompletedTask; });
        await LocalWriterChecks.ExpectAsync<IOException>(() =>
            writer.PreserveAsync(victim, "unbound-action", CancellationToken.None));
        LocalChecks.Require(File.ReadAllText(stage) == "unbound bytes" &&
            File.ReadAllText(Path.Combine(local, "file.txt")) == "original" &&
            notices.Any(item => item.Detail.Contains(stage, StringComparison.Ordinal) &&
                item.Detail.Contains("Move this staging file aside", StringComparison.Ordinal)),
            "unbound Recovery stage retains both files and emits a path-specific actionable notice");
    }

    private static async Task MismatchedStageAsync(string root)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(LocalRecoveryInterruptionChecks).Assembly.Location);
        start.ArgumentList.Add("--v2-recovery-kill-child");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("mismatched");
        using var child = Process.Start(start) ?? throw new IOException("Mismatched stage child did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            throw;
        }
        LocalChecks.Require(child.ExitCode == 73, "mismatched stage starts with real interrupted envelope: " + await stdout + await stderr);
        var recovery = Path.Combine(root, "recovery");
        var stage = Path.Combine(recovery, ".syncshark-recovery-" + LocalChecks.PairId,
            LocalChecks.OperationId("interrupted-action") + ".recovery.part");
        // Save the genuine envelope, repair access to move its old object aside, then put those
        // same encrypted bytes back beside a different native object (never fake an ownership ID).
        var envelope = await File.ReadAllBytesAsync(SyncLocalFileAuthority.RecoverySidecarPath(stage));
        SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stage, stage);
        File.Move(stage, stage + ".retained");
        await File.WriteAllTextAsync(stage, "foreign replacement");
        await File.WriteAllBytesAsync(SyncLocalFileAuthority.RecoverySidecarPath(stage), envelope);
        var local = Path.Combine(root, "local");
        var victim = JsonSerializer.Deserialize<ItemState>(await File.ReadAllTextAsync(Path.Combine(root, "victim.json")))!;
        var writer = await LocalChecks.OpenWriter(local, recovery, CancellationToken.None);
        var notices = new List<SyncByteProgress>();
        using var progress = SyncByteProgressStream.Begin("file.bin", item =>
        { notices.Add(item); return Task.CompletedTask; });
        await LocalWriterChecks.ExpectAsync<IOException>(() => writer.PreserveAsync(victim, "interrupted-action", CancellationToken.None));
        LocalChecks.Require(await File.ReadAllTextAsync(stage) == "foreign replacement" &&
            File.ReadAllBytes(Path.Combine(local, "file.bin")).SequenceEqual(Bytes()) &&
            File.ReadAllBytes(SyncLocalFileAuthority.RecoverySidecarPath(stage)).SequenceEqual(envelope) &&
            notices.Any(item => item.Detail.Contains(stage, StringComparison.Ordinal) &&
                item.Detail.Contains("Move this staging file aside", StringComparison.Ordinal)),
            "mismatched-envelope stage keeps foreign bytes, original and envelope with actionable notice");
    }

    private static async Task EnvelopeFailureKindsAsync(string root)
    {
        foreach (var kind in new[] { "corrupt-length", "corrupt-dpapi", "sharing" })
        {
            var fixture = Path.Combine(root, kind);
            var local = Path.Combine(fixture, "local");
            var recovery = Path.Combine(fixture, "recovery");
            Directory.CreateDirectory(local);
            await File.WriteAllTextAsync(Path.Combine(local, "file.txt"), "original");
            var writer = await LocalChecks.OpenWriter(local, recovery, CancellationToken.None);
            var victim = (await writer.ObserveAsync("file.txt", CancellationToken.None))!;
            var stage = Path.Combine(recovery, ".syncshark-recovery-" + LocalChecks.PairId,
                LocalChecks.OperationId("envelope-action") + ".recovery.part");
            Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
            await File.WriteAllTextAsync(stage, "retained stage");
            var sidecar = SyncLocalFileAuthority.RecoverySidecarPath(stage);
            var envelope = kind == "corrupt-length" ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4, 5 };
            await File.WriteAllBytesAsync(sidecar, envelope);
            using var held = kind == "sharing"
                ? new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            var notices = new List<SyncByteProgress>();
            using var progress = SyncByteProgressStream.Begin("file.txt", item =>
            { notices.Add(item); return Task.CompletedTask; });
            await LocalWriterChecks.ExpectAsync<IOException>(() =>
                writer.PreserveAsync(victim, "envelope-action", CancellationToken.None));
            var moveAside = notices.Any(item => item.Detail.Contains(stage, StringComparison.Ordinal) &&
                item.Detail.Contains("Move this staging file aside", StringComparison.Ordinal));
            LocalChecks.Require(moveAside == (kind != "sharing") &&
                File.ReadAllText(stage) == "retained stage" &&
                File.ReadAllText(Path.Combine(local, "file.txt")) == "original",
                kind + ": only proven envelope corruption gets move-aside advice; stage and original stay intact");
        }
    }

    private static async Task DownloadOutageAsync(string root)
    {
        var local = Path.Combine(root, "local");
        var spool = Path.Combine(root, "spool");
        Directory.CreateDirectory(local);
        var writer = await LocalChecks.OpenWriter(local, Path.Combine(root, "recovery"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(local, "file.bin"), "original");
        var victim = (await writer.ObserveAsync("file.bin", CancellationToken.None))!;
        var bytes = Bytes();
        var hash = LocalChecks.BlockHash(bytes);
        using var handler = new OutageHandler(bytes, hash);
        using var http = new HttpClient(handler);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, "id:pilot-folder");
        var cloud = new DropboxWriter(new RootDef("cloud", "Dropbox", "account", "/Pilot", "id:pilot-folder"),
            Side.B, client, spool, isStateDamage: StorageFailureClassifier.IsStateDamage);
        // DropboxContractHandler binds its immutable download and metadata to /Pilot/wire.txt.
        var source = new ItemState("wire.txt", ItemKind.File, "id:wire", "revision", hash, bytes.Length,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        Console.WriteLine("Download outage fixture expected: " + JsonSerializer.Serialize(source));
        await LocalWriterChecks.ExpectAsync<IOException>(async () =>
        {
            await using var content = await cloud.OpenReadAsync(source, CancellationToken.None);
            throw new InvalidOperationException("An interrupted download returned a readable spool.");
        });
        LocalChecks.Require(!Directory.EnumerateFileSystemEntries(spool).Any() &&
            File.ReadAllText(Path.Combine(local, "file.bin")) == "original",
            "partial download outage removes spool and never publishes a half target");
        await using (var content = await cloud.OpenReadAsync(source, CancellationToken.None))
        {
            LocalChecks.Require(content.Position == 0 && content.Length == bytes.Length,
                "download rerun starts from a newly verified full spool");
            await writer.PutAsync(new SyncAction("outage-download", 2, ActionKind.Update, Side.A, "file.bin", null,
                source, victim, false, null), content, CancellationToken.None);
        }
        LocalChecks.Require(File.ReadAllBytes(Path.Combine(local, "file.bin")).SequenceEqual(bytes) &&
            !Directory.EnumerateFileSystemEntries(spool).Any() && handler.Attempts == 2,
            "outage rerun publishes exact downloaded bytes and releases spool");
    }

    private sealed class OutageHandler(byte[] bytes, string hash) : HttpMessageHandler
    {
        private readonly DropboxContractHandler _wire = new()
        {
            DownloadBytes = bytes, DownloadHash = hash, DownloadRevision = "revision", MetadataRevision = "revision",
            ArchiveHash = hash, ArchiveSize = bytes.Length
        };
        internal int Attempts { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var invoker = new HttpMessageInvoker(_wire, disposeHandler: false);
            var response = await invoker.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/2/files/get_metadata")
                Console.WriteLine("Download outage fixture metadata: " + await response.Content.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath == "/2/files/download" && ++Attempts == 1)
            {
                response.Content.Dispose();
                response.Content = new StreamContent(new PartialBody(bytes));
            }
            return response;
        }
        protected override void Dispose(bool disposing)
        { if (disposing) _wire.Dispose(); base.Dispose(disposing); }
    }

    private sealed class PartialBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position == 0 ? base.ReadAsync(buffer[..1], ct) : ValueTask.FromException<int>(new IOException("Injected outage"));
    }

    private static byte[] Bytes() => Enumerable.Range(0, 250_000).Select(i => (byte)(i % 251)).ToArray();
}
