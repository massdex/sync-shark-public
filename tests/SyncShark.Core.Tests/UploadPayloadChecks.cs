using System.Net;
using System.Text.Json;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class UploadPayloadChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task RunAsync()
    {
        var original = UploadPayloadBudget.Capacity;
        try
        {
            var failures = new List<Exception>();
            foreach (var check in new Func<Task>[] { ManyUploadsAsync, OversizedAsync, OversizedSessionAsync, CancellationAsync, ContentResendAsync, FifoAndSettingsAsync })
            {
                try { await check(); }
                catch (Exception error) { Console.WriteLine($"FAIL {check.Method.Name}: {error.GetBaseException().Message}"); failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
            Console.WriteLine("payload checks passed: 64 bounded uploads; oversized; independent download; cancellation; retry/source lifetime");
        }
        finally { UploadPayloadBudget.Capacity = original; }
    }

    private static async Task<(string, int)> Stage(DropboxPilotClient client, Stream source, CancellationToken ct = default)
    {
        var result = await client.UploadBatchedAsync("upload", source, "test-hash", null,
            V2Settings.Default with { UploadSessionStartConcurrency = 128, DropboxSessionStartRetryBaseDelayMs = 0,
                UploadBatchSize = 1 }, ct);
        return (result.FileId, checked((int)result.Size));
    }

    private static DropboxPilotClient Client(HttpClient http) => new(http, new FixedTokenSource(), "/Pilot",
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace",
            "home-namespace", DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes), "id:root");

    private static async Task ManyUploadsAsync()
    {
        UploadPayloadBudget.Capacity = 128 * 1024;
        using var wire = new Wire { Hold = true };
        using var http = new HttpClient(wire);
        var clients = new[] { Client(http), Client(http) };
        var sources = Enumerable.Range(0, 64).Select(_ => new GeneratedSource(8 * 1024 * 1024, 64 * 1024)).ToArray();
        var tasks = sources.Select((source, i) => Stage(clients[i % 2], source)).ToArray();
        try
        {
            await Task.WhenAny(wire.Entered.Task, Task.WhenAll(tasks)).Unwrap().WaitAsync(Timeout);
            Require(UploadPayloadBudget.Used == 128 * 1024, "64 callers share the process cap across clients");
            Require(sources.Count(x => x.Position > 0) == 2, "waiting uploads must not read/allocate whole files");
            // Exercise the real download loop to a disk file while ALL upload bytes are held.
            var path = Path.Combine(Path.GetTempPath(), "syncshark-payload-" + Guid.NewGuid().ToString("N"));
            await using var disk = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            var expected = new PilotRemoteEntry("download", PilotEntryKind.File, "id:download", "r1", 16, wire.Hash);
            await clients[0].DownloadAsync(expected, disk).WaitAsync(Timeout);
            Require(disk.Length == 16 && tasks.All(t => !t.IsCompleted), "download progresses independently while uploads are held");
        }
        finally { wire.Release.TrySetResult(); }
        var results = await Task.WhenAll(tasks).WaitAsync(Timeout);
        Require(results.All(x => x.Item2 == 8 * 1024 * 1024) && UploadPayloadBudget.Used == 0, "all staged bytes and reservations complete");
        Console.WriteLine("PASS 64 uploads bounded across two clients + real download to disk concurrently");
    }

    private static async Task OversizedAsync()
    {
        UploadPayloadBudget.Capacity = 16 * 1024;
        using var wire = new Wire { Retry = true };
        using var http = new HttpClient(wire);
        using var source = new GeneratedSource(1024 * 1024, 16 * 1024);
        var result = await Stage(Client(http), source).WaitAsync(Timeout);
        Require(result.Item2 == source.Length && wire.Starts == 2 && !source.Disposed && UploadPayloadBudget.Used == 0,
            "file larger than budget streams and retries from zero without closing source or leaking admission");
        Console.WriteLine("PASS oversized payload + replay after rejected staging request");
    }

    private static async Task OversizedSessionAsync()
    {
        UploadPayloadBudget.Capacity = 4;
        using var wire = new DropboxUploadSessionContractHandler();
        using var http = new HttpClient(wire);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot",
            new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace",
                "home-namespace", DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes),
            "id:pilot-folder", uploadSessionThresholdBytes: 4, uploadSessionChunkSizeBytes: 8);
        var hash = await DropboxContentHash.ComputeAsync(new MemoryStream(new byte[20]));
        using var source = new GeneratedSource(20, 4);
        var result = await client.UploadResumableAsync("large.bin", source, hash, null, null,
            static (_, _) =>
            {
                Require(UploadPayloadBudget.Used == 0, "no payload lease survives into a durable checkpoint wait");
                return Task.CompletedTask;
            }).WaitAsync(Timeout);
        Require(result.Size == 20 && wire.CommittedBytes.SequenceEqual(new byte[20]) && UploadPayloadBudget.Used == 0,
            "public resumable upload chunks a file and configured chunk larger than byte budget without truncation");
        Console.WriteLine("PASS public oversized resumable upload with chunk larger than budget");
    }

    private static async Task CancellationAsync()
    {
        UploadPayloadBudget.Capacity = 16 * 1024;
        using var wire = new Wire { Hold = true };
        using var http = new HttpClient(wire);
        using var activeCancel = new CancellationTokenSource();
        using var waitingCancel = new CancellationTokenSource();
        var client = Client(http);
        var active = Stage(client, new GeneratedSource(1024 * 1024, 16 * 1024), activeCancel.Token);
        await Task.WhenAny(wire.Entered.Task, active).Unwrap().WaitAsync(Timeout);
        var waitingSource = new GeneratedSource(1024 * 1024, 16 * 1024);
        var waiting = Stage(client, waitingSource, waitingCancel.Token);
        Require(waitingSource.Position == 0 && !waiting.IsCompleted, "waiter reads nothing before admission");
        waitingCancel.Cancel(); activeCancel.Cancel();
        foreach (var task in new[] { active, waiting })
        {
            try { await task.WaitAsync(Timeout); throw new InvalidOperationException("cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        wire.Hold = false; wire.Release.TrySetResult();
        await Stage(client, new GeneratedSource(1024, 16 * 1024)).WaitAsync(Timeout);
        Require(UploadPayloadBudget.Used == 0, "active/waiter cancellation releases all bytes and next transfer runs");
        Console.WriteLine("PASS cancellation of active and waiting uploads without deadlock");
    }

    private static async Task ContentResendAsync()
    {
        using var source = new MemoryStream(new byte[] { 99, 98, 1, 2, 3, 4, 97 });
        source.Position = 2;
        using var content = new UploadPayloadContent(source, 4);
        using var first = new MemoryStream(); using var second = new MemoryStream();
        await content.CopyToAsync(first); await content.CopyToAsync(second);
        Require(first.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 }) && first.ToArray().SequenceEqual(second.ToArray()),
            "same request serialization must rewind to original nonzero offset and stop at segment end");
        Require(source.CanRead && content.Headers.ContentLength == 4 && UploadPayloadBudget.Used == 0, "stable content length and caller-owned source");
        Console.WriteLine("PASS same-request resend of exact nonzero source segment");
    }

    private static async Task FifoAndSettingsAsync()
    {
        var settings = JsonSerializer.Deserialize<SyncTransferSettings>("{\"UploadPayloadBudgetBytes\":8}")!;
        var limiter = new SyncTransferRateLimiter(settings);
        Require(UploadPayloadBudget.Capacity == 8, "persisted transfer configuration initializes process budget");
        _ = new SyncTransferRateLimiter(new SyncTransferSettings());
        Require(UploadPayloadBudget.Capacity == 8, "default client construction does not reset configured process budget");
        using var held = await UploadPayloadBudget.RentAsync(4, default);
        var big = UploadPayloadBudget.RentAsync(8, default);
        using var cancelled = new CancellationTokenSource();
        var middle = UploadPayloadBudget.RentAsync(1, cancelled.Token);
        var small = UploadPayloadBudget.RentAsync(1, default);
        Require(!big.IsCompleted && !small.IsCompleted, "small arrival cannot jump waiting large request");
        cancelled.Cancel();
        try { await middle; throw new InvalidOperationException("queued cancellation ignored"); } catch (OperationCanceledException) { }
        held.Dispose();
        using var largeLease = await big.WaitAsync(Timeout);
        Require(!small.IsCompleted, "FIFO large request reserves capacity before next small request");
        largeLease.Dispose();
        using var smallLease = await small.WaitAsync(Timeout);
        smallLease.Dispose();
        limiter.Update(settings with { UploadPayloadBudgetBytes = 0 });
        Require(UploadPayloadBudget.Capacity == 256L * 1024 * 1024 && UploadPayloadBudget.Used == 0,
            "live settings update resets default without leaked or negative reservations");
        Console.WriteLine("PASS FIFO admission, cancelled waiter, persisted configuration and live reset");
    }

    private sealed class GeneratedSource(long size, int maximumRead) : Stream
    {
        internal bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Require(count <= maximumRead, $"unbounded source read: {count} > {maximumRead}");
            var read = (int)Math.Min(count, size - Position); buffer.AsSpan(offset, read).Clear(); Position += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(buffer.Length <= maximumRead, $"unbounded source read: {buffer.Length} > {maximumRead}");
            var read = (int)Math.Min(buffer.Length, size - Position); buffer.Span[..read].Clear(); Position += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal bool Hold, Retry;
        internal int Starts;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly string Hash = DropboxContentHash.ComputeAsync(new MemoryStream(new byte[16])).GetAwaiter().GetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/2/users/get_current_account") return Json(new { account_id = "account-id", root_info = new { root_namespace_id = "root-namespace", home_namespace_id = "home-namespace" } });
            if (route == "/2/files/get_metadata") return Json(new Dictionary<string, object> { [".tag"] = "folder", ["id"] = "id:root", ["name"] = "Pilot", ["path_display"] = "/Pilot", ["path_lower"] = "/pilot" });
            if (route == "/2/files/download")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) };
                response.Headers.Add("Dropbox-API-Result", JsonSerializer.Serialize(new Dictionary<string, object> { [".tag"] = "file", ["id"] = "id:download", ["name"] = "download", ["path_display"] = "/Pilot/download", ["path_lower"] = "/pilot/download", ["rev"] = "r1", ["size"] = 16, ["content_hash"] = Hash }));
                return response;
            }
            if (route == "/2/files/upload_session/finish_batch_v2")
            {
                Require(UploadPayloadBudget.Used == 0 || Hold, "payload admission must end before metadata commit");
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                return Json(new { entries = document.RootElement.GetProperty("entries").EnumerateArray().Select(entry =>
                    new Dictionary<string, object> { [".tag"] = "success", ["id"] = "id:uploaded", ["rev"] = "r1",
                        ["size"] = entry.GetProperty("cursor").GetProperty("offset").GetInt64(), ["content_hash"] = "test-hash",
                        ["path_display"] = entry.GetProperty("commit").GetProperty("path").GetString()! }).ToArray() });
            }
            Require(route == "/2/files/upload_session/start", route);
            var attempt = Interlocked.Increment(ref Starts);
            await request.Content!.CopyToAsync(new Sink(this), ct);
            if (Retry && attempt == 1) return new(HttpStatusCode.TooManyRequests);
            return Json(new { session_id = "session" + attempt });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
        private sealed class Sink(Wire owner) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            { owner.Entered.TrySetResult(); if (owner.Hold) await owner.Release.Task.WaitAsync(ct); }
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
