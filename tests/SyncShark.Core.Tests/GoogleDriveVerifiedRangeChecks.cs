using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class GoogleDriveVerifiedRangeChecks
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("0123456789abcdef");
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    // Runs serially with the other spool-admission fixtures: its capacity override is process-wide.
    internal static async Task RunAsync(string scratchRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Verified staging uses Windows NTFS leases.");
        var parent = Path.GetFullPath(scratchRoot);
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, "google-range-" + Guid.NewGuid().ToString("N"));
        var volume = Path.GetPathRoot(root)!;
        Require(SyncRecoverySpoolAdmission.SnapshotForTest(volume) == (0, 0, 0), "spool fixtures require an idle admission volume");
        var capacity = SyncRecoverySpoolAdmission.CapacityBytesForTest;
        var timeout = SyncRecoverySpoolAdmission.WaitTimeoutForTest;
        SyncRecoverySpoolAdmission.CapacityBytesForTest = 4 * 1024 * 1024;
        SyncRecoverySpoolAdmission.WaitTimeoutForTest = Bound;
        try
        {
            await SuccessfulRangesAsync(root);
            await BoundsAsync(root);
            await VerificationFailuresAsync(root);
            await LowCapacityAsync(root);
            await ConcurrentRangesAsync(root);
            await CancellationAsync(root);
            await RetryAndIdentityAsync(root);
            Clean(root);
        }
        finally
        {
            SyncRecoverySpoolAdmission.CapacityBytesForTest = capacity;
            SyncRecoverySpoolAdmission.WaitTimeoutForTest = timeout;
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        Console.WriteLine("PASS Google verified range checks: real full download, zero unverified output, nonseekable destinations, exact identity, coalescing, cancellation, reservation and own-temp cleanup");
    }

    private static PilotRemoteEntry Expected(string id = "range-file", string version = "1", long? size = null,
        string? hash = null) => new("example.bin", PilotEntryKind.File, id,
        JsonSerializer.Serialize(new[] { id, version }), size ?? Bytes.Length, hash ?? Hash(Bytes));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static GoogleDrivePilotClient Client(HttpClient http, string root, string permission = "range-account")
    {
        var binding = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permission), permission, "fixture@example.invalid", "Fixture",
            [GoogleDriveProviderConfiguration.DriveScope], CredentialReference: "synthetic-only").Normalize();
        return new GoogleDrivePilotClient(http, new Tokens(), binding, "range-root", "/Pair")
        { VerifiedRangeStagingRootForTest = root };
    }

    private static async Task SuccessfulRangesAsync(string root)
    {
        using var protocol = new Protocol(); using var http = new HttpClient(protocol);
        var client = Client(http, root);
        foreach (var (offset, length) in new[] { (0L, 16L), (3L, 5L), (15L, 1L) })
        {
            using var output = new WriteOnly();
            var result = await client.DownloadRangeAsync(Expected(), offset, length, output);
            Require(output.Bytes.SequenceEqual(Bytes.Skip((int)offset).Take((int)length)), "only exact requested bytes are delivered");
            Require(result.FileId == "range-file" && result.Revision == Expected().Revision && result.TotalSize == 16 &&
                result.ContentHash == Hash(Bytes) && result.Offset == offset && result.Length == length, "result is bound to real verified metadata");
            Clean(root);
        }
        Require(protocol.MediaCalls == 3 && protocol.MetadataCalls == 6, "sequential cohorts revalidate and redownload rather than cache");
        using var upper = new WriteOnly();
        await client.DownloadRangeAsync(Expected(hash: Hash(Bytes).ToUpperInvariant()), 0, 1, upper);
        Require(upper.Bytes.SequenceEqual(Bytes.Take(1)), "SHA-256 case normalization preserves the content identity");
        Clean(root);
    }

    private static async Task BoundsAsync(string root)
    {
        using var protocol = new Protocol(); using var http = new HttpClient(protocol); var client = Client(http, root);
        foreach (var (offset, length) in new[] { (-1L, 1L), (0L, 0L), (16L, 1L), (15L, 2L), (long.MaxValue, 2L), (1L, long.MaxValue) })
        {
            using var output = new WriteOnly();
            await RejectAsync(() => client.DownloadRangeAsync(Expected(), offset, length, output));
            Require(output.Bytes.Length == 0, "invalid bounds emit no bytes");
        }
        foreach (var invalid in new[] { Expected(size: long.MaxValue), Expected(size: -1),
            Expected() with { Id = null }, Expected() with { Revision = null }, Expected(hash: "bad"),
            Expected() with { Kind = PilotEntryKind.Directory } })
        {
            using var output = new WriteOnly(); await RejectAsync(() => client.DownloadRangeAsync(invalid, 0, 1, output));
            Require(output.Bytes.Length == 0, "invalid identity emits no bytes");
        }
        using var readOnly = new MemoryStream(Bytes, writable: false);
        await RejectAsync(() => client.DownloadRangeAsync(Expected(), 0, 1, readOnly));
        Require(protocol.Requests == 0, "bounds and identity fail before provider reads"); Clean(root);
    }

    private static async Task VerificationFailuresAsync(string root)
    {
        foreach (var configure in new Action<Protocol>[]
        {
            p => p.Delivered = Encoding.UTF8.GetBytes("fedcba9876543210"),
            p => p.Delivered = Bytes[..^1], p => p.Delivered = [.. Bytes, 1],
            p => p.InitialVersion = "2", p => p.AfterVersion = "2",
            p => p.AfterTrashed = true, p => p.AfterHash = new string('0', 64),
            p => p.AfterParents = ["outside-root"], p => p.AfterId = "other-file",
            p => p.DeclaredHash = "", p => p.MediaStatus = HttpStatusCode.NotFound
        })
        {
            using var protocol = new Protocol(); configure(protocol); using var http = new HttpClient(protocol);
            using var output = new WriteOnly();
            await RejectAsync(() => Client(http, root).DownloadRangeAsync(Expected(), 2, 4, output));
            Require(output.Bytes.Length == 0, "failed real checksum, length, revision or post-read metadata emits zero output"); Clean(root);
        }
        foreach (var expected in new[] { Expected(size: 15), Expected(size: 17), Expected(hash: new string('0', 64)) })
        {
            using var protocol = new Protocol(); using var http = new HttpClient(protocol); using var output = new WriteOnly();
            await RejectAsync(() => Client(http, root).DownloadRangeAsync(expected, 0, 1, output));
            Require(output.Bytes.Length == 0, "the provider's valid object cannot replace a different reviewed size/hash"); Clean(root);
        }
    }

    private static async Task LowCapacityAsync(string root)
    {
        var capacity = SyncRecoverySpoolAdmission.CapacityBytesForTest;
        SyncRecoverySpoolAdmission.CapacityBytesForTest = 31; // Twice the 16-byte object requires 32.
        try
        {
            using var protocol = new Protocol(); using var http = new HttpClient(protocol); using var output = new WriteOnly();
            await RejectAsync(() => Client(http, root).DownloadRangeAsync(Expected(), 0, 1, output));
            Require(protocol.Requests == 0 && output.Bytes.Length == 0, "2x admission fails before transfer or delivery"); Clean(root);
        }
        finally { SyncRecoverySpoolAdmission.CapacityBytesForTest = capacity; }
    }

    private static async Task ConcurrentRangesAsync(string root)
    {
        using var protocol = new Protocol { HoldMedia = true }; using var http = new HttpClient(protocol);
        var client = Client(http, root); using var first = new WriteOnly(); using var second = new WriteOnly();
        var a = client.DownloadRangeAsync(Expected(), 1, 8, first);
        await protocol.MediaEntered.Task.WaitAsync(Bound);
        var b = client.DownloadRangeAsync(Expected(), 5, 7, second);
        Require(first.Bytes.Length == 0 && second.Bytes.Length == 0 && protocol.MediaCalls == 1,
            "overlapping callers wait behind one whole-object transport without early output");
        Require(SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(root)!).ReservedBytes == 32,
            "one exact cohort owns one conservative 2x reservation");
        protocol.ReleaseMedia.TrySetResult(true);
        await Task.WhenAll(a, b).WaitAsync(Bound);
        Require(first.Bytes.SequenceEqual(Bytes.Skip(1).Take(8)) && second.Bytes.SequenceEqual(Bytes.Skip(5).Take(7)),
            "each coalesced caller receives its own overlapping range");
        Require(protocol.MediaCalls == 1, "coalescing uses one real DownloadAsync"); Clean(root);
    }

    private static async Task CancellationAsync(string root)
    {
        using (var protocol = new Protocol())
        using (var http = new HttpClient(protocol))
        using (var output = new WriteOnly())
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await CancelledAsync(() => Client(http, root).DownloadRangeAsync(Expected(), 0, 4, output, cancelled.Token));
            Require(protocol.Requests == 0 && output.Bytes.Length == 0, "pre-cancellation does no I/O"); Clean(root);
        }
        using (var protocol = new Protocol { HoldMedia = true })
        using (var http = new HttpClient(protocol))
        using (var first = new WriteOnly())
        using (var second = new WriteOnly())
        using (var cancelled = new CancellationTokenSource())
        {
            var client = Client(http, root);
            var a = client.DownloadRangeAsync(Expected(), 0, 4, first, cancelled.Token);
            await protocol.MediaEntered.Task.WaitAsync(Bound);
            var b = client.DownloadRangeAsync(Expected(), 4, 4, second);
            cancelled.Cancel(); await CancelledAsync(() => a);
            Require(!protocol.UnderlyingCancelled && first.Bytes.Length == 0, "one waiter cannot cancel another's download");
            protocol.ReleaseMedia.TrySetResult(true); await b.WaitAsync(Bound);
            Require(second.Bytes.SequenceEqual(Bytes.Skip(4).Take(4)) && protocol.MediaCalls == 1, "remaining waiter completes"); Clean(root);
        }
        using (var protocol = new Protocol { HoldMedia = true })
        using (var http = new HttpClient(protocol))
        using (var output = new WriteOnly())
        using (var cancelled = new CancellationTokenSource())
        {
            var task = Client(http, root).DownloadRangeAsync(Expected(), 0, 4, output, cancelled.Token);
            await protocol.MediaEntered.Task.WaitAsync(Bound);
            cancelled.Cancel(); await CancelledAsync(() => task);
            Require(protocol.UnderlyingCancelled && output.Bytes.Length == 0, "last waiter cancels the transport and delivers nothing"); Clean(root);
        }
        using (var protocol = new Protocol())
        using (var http = new HttpClient(protocol))
        using (var cancelled = new CancellationTokenSource())
        using (var output = new WriteOnly(beforeWrite: () => cancelled.Cancel()))
        {
            await CancelledAsync(() => Client(http, root).DownloadRangeAsync(Expected(), 0, 4, output, cancelled.Token));
            Require(protocol.MediaCalls == 1 && protocol.MetadataCalls == 2 && output.Bytes.Length == 0,
                "cancellation at delivery follows full verification but writes no bytes"); Clean(root);
        }
        var large = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
        using (var protocol = new Protocol { Source = large, Delivered = large, DeclaredHash = Hash(large) })
        using (var http = new HttpClient(protocol))
        using (var cancelled = new CancellationTokenSource())
        {
            var writes = 0;
            using var output = new WriteOnly(beforeWrite: () => { if (++writes == 2) cancelled.Cancel(); });
            await CancelledAsync(() => Client(http, root).DownloadRangeAsync(Expected(size: large.Length, hash: Hash(large)),
                0, large.Length, output, cancelled.Token));
            Require(output.Bytes.Length == 128 * 1024 && output.Bytes.SequenceEqual(large.Take(128 * 1024)),
                "mid-copy cancellation may expose only an already whole-file-verified prefix, never unverified data"); Clean(root);
        }
        using (var protocol = new Protocol())
        using (var http = new HttpClient(protocol))
        using (var output = new WriteOnly(beforeWrite: () => throw new IOException("Synthetic destination refusal")))
        {
            await RejectAsync(() => Client(http, root).DownloadRangeAsync(Expected(), 0, 4, output));
            Require(output.Bytes.Length == 0, "destination failure still releases the verified stage and reservation"); Clean(root);
        }
        // Hold one verified copy so a second waiter is cancelled at the copy gate, independently.
        using (var protocol = new Protocol { HoldMedia = true })
        using (var http = new HttpClient(protocol))
        using (var first = new WriteOnly(holdWrite: true))
        using (var second = new WriteOnly())
        using (var cancelled = new CancellationTokenSource())
        {
            var client = Client(http, root);
            var a = client.DownloadRangeAsync(Expected(), 0, 4, first);
            await protocol.MediaEntered.Task.WaitAsync(Bound);
            protocol.ReleaseMedia.TrySetResult(true);
            await first.WriteEntered.Task.WaitAsync(Bound);
            var b = client.DownloadRangeAsync(Expected(), 4, 4, second, cancelled.Token);
            cancelled.Cancel(); await CancelledAsync(() => b);
            first.ReleaseWrite.TrySetResult(true); await a.WaitAsync(Bound);
            Require(second.Bytes.Length == 0 && first.Bytes.SequenceEqual(Bytes.Take(4)), "copy-waiter cancellation does not dispose another lease's stage"); Clean(root);
        }
    }

    private static async Task RetryAndIdentityAsync(string root)
    {
        using (var protocol = new Protocol { Delivered = Bytes[..^1] })
        using (var http = new HttpClient(protocol))
        {
            var client = Client(http, root); using var bad = new WriteOnly();
            await RejectAsync(() => client.DownloadRangeAsync(Expected(), 0, 4, bad)); Clean(root);
            protocol.Delivered = Bytes; using var good = new WriteOnly();
            await client.DownloadRangeAsync(Expected(), 0, 4, good);
            Require(protocol.MediaCalls == 2 && bad.Bytes.Length == 0 && good.Bytes.SequenceEqual(Bytes.Take(4)),
                "failed cohorts do not poison a later verified retry"); Clean(root);
        }
        foreach (var different in new[] { Expected(hash: new string('0', 64)), Expected(size: 17), Expected(version: "2") })
        {
            using var protocol = new Protocol { HoldMedia = true }; using var http = new HttpClient(protocol);
            using var first = new WriteOnly(); using var second = new WriteOnly(); var client = Client(http, root);
            var a = client.DownloadRangeAsync(Expected(), 0, 1, first);
            await protocol.MediaEntered.Task.WaitAsync(Bound);
            var b = client.DownloadRangeAsync(different, 0, 1, second);
            if (different.Revision != Expected().Revision)
            {
                // This must reject independently, without waiting for another revision's transport.
                await RejectAsync(() => b);
                Require(protocol.MediaCalls == 1, "another revision cannot inherit an in-flight content decision");
                protocol.ReleaseMedia.TrySetResult(true);
            }
            else
            {
                await protocol.SecondMediaEntered.Task.WaitAsync(Bound);
                protocol.ReleaseMedia.TrySetResult(true); await RejectAsync(() => b);
            }
            await a.WaitAsync(Bound);
            Require(second.Bytes.Length == 0 && first.Bytes.SequenceEqual(Bytes.Take(1)),
                "a different expected hash, size or revision cannot share a verified content lease"); Clean(root);
        }
        using (var protocol = new Protocol { HoldMedia = true })
        using (var http = new HttpClient(protocol))
        using (var first = new WriteOnly())
        using (var second = new WriteOnly())
        {
            var client = Client(http, root);
            var a = client.DownloadRangeAsync(Expected(), 0, 1, first);
            await protocol.MediaEntered.Task.WaitAsync(Bound);
            var b = client.DownloadRangeAsync(Expected(id: "other-file"), 0, 1, second);
            await protocol.SecondMediaEntered.Task.WaitAsync(Bound);
            Require(protocol.MediaCalls == 2, "different file identities cannot join a cohort");
            protocol.ReleaseMedia.TrySetResult(true); await Task.WhenAll(a, b).WaitAsync(Bound); Clean(root);
        }
        using (var protocol = new Protocol { HoldMedia = true })
        using (var http = new HttpClient(protocol))
        using (var first = new WriteOnly())
        using (var second = new WriteOnly())
        {
            var a = Client(http, root, "account-one").DownloadRangeAsync(Expected(), 0, 1, first);
            await protocol.MediaEntered.Task.WaitAsync(Bound);
            var b = Client(http, root, "account-two").DownloadRangeAsync(Expected(), 0, 1, second);
            await protocol.SecondMediaEntered.Task.WaitAsync(Bound);
            Require(protocol.MediaCalls == 2, "separate bound clients/accounts never share their cohort");
            protocol.ReleaseMedia.TrySetResult(true); await Task.WhenAll(a, b).WaitAsync(Bound); Clean(root);
        }
    }

    private static void Clean(string root)
    {
        Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "only own DeleteOnClose staging files are removed");
        Require(SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(root)!) == (0, 0, 0), "all reservations/waiters are released");
    }
    private static void Require(bool value, string reason) { if (!value) throw new FixtureException(reason); }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action().WaitAsync(Bound); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or HttpRequestException) { return; }
        throw new FixtureException("Expected a production rejection.");
    }
    private static async Task CancelledAsync(Func<Task> action)
    {
        try { await action().WaitAsync(Bound); }
        catch (OperationCanceledException) { return; }
        throw new FixtureException("Expected cancellation.");
    }
    private sealed class FixtureException(string reason) : Exception(reason);
    private sealed class Tokens : IGoogleDriveAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult("synthetic-not-a-credential"); } }

    private sealed class Protocol : HttpMessageHandler
    {
        internal byte[] Source = Bytes;
        internal byte[] Delivered = Bytes;
        internal string InitialVersion = "1";
        internal string AfterVersion = "1";
        internal string? AfterHash;
        internal string? AfterId;
        internal string[] AfterParents = ["range-root"];
        internal bool AfterTrashed;
        internal string DeclaredHash = Hash(Bytes);
        internal HttpStatusCode MediaStatus = HttpStatusCode.OK;
        internal bool HoldMedia;
        internal bool UnderlyingCancelled;
        internal int Requests, MediaCalls, MetadataCalls;
        internal readonly TaskCompletionSource<bool> MediaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> SecondMediaEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReleaseMedia = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Require(request.Method == HttpMethod.Get && request.Headers.Range is null && request.Content is null,
                "verified range delivery uses read-only whole-file GET, never an unpinned Range");
            Require(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "www.googleapis.com" &&
                request.RequestUri.AbsolutePath.StartsWith("/drive/v3/files/", StringComparison.Ordinal), "exact Google media/metadata origin");
            var id = Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split('/')[^1]);
            if (request.RequestUri.Query.Contains("alt=media", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref MediaCalls) == 2) SecondMediaEntered.TrySetResult(true);
                MediaEntered.TrySetResult(true);
                if (HoldMedia)
                {
                    try { await ReleaseMedia.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) { UnderlyingCancelled = true; throw; }
                }
                return new HttpResponseMessage(MediaStatus) { Content = new ByteArrayContent(Delivered) };
            }
            if (id == "range-root") return Json(new { id, name = "Pair", mimeType = "application/vnd.google-apps.folder", version = "1", trashed = false });
            var after = false;
            lock (_reads) { var count = _reads.GetValueOrDefault(id) + 1; _reads[id] = count; after = count % 2 == 0; }
            Interlocked.Increment(ref MetadataCalls);
            var version = after ? AfterVersion : InitialVersion;
            return Json(new
            {
                id = after ? AfterId ?? id : id, name = "example.bin", mimeType = "application/octet-stream", version,
                headRevisionId = "head-" + version, size = Source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sha256Checksum = after ? AfterHash ?? DeclaredHash : DeclaredHash,
                parents = after ? AfterParents : ["range-root"], trashed = after && AfterTrashed,
                capabilities = new { canDownload = true }
            });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    // Position/Length/Seek/Read all throw: success proves that the real range method supports CFAPI's
    // nonseekable write-only destination instead of accidentally passing a MemoryStream-only test.
    private sealed class WriteOnly(Action? beforeWrite = null, bool holdWrite = false) : Stream
    {
        private readonly MemoryStream _bytes = new();
        internal byte[] Bytes => _bytes.ToArray();
        internal readonly TaskCompletionSource<bool> WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new FixtureException("Destination Length must not be read.");
        public override long Position { get => throw new FixtureException("Destination Position must not be read."); set => throw new FixtureException("Destination Position must not be set."); }
        public override int Read(byte[] buffer, int offset, int count) => throw new FixtureException("Destination must not be read.");
        public override long Seek(long offset, SeekOrigin origin) => throw new FixtureException("Destination must not be sought.");
        public override void SetLength(long value) => throw new FixtureException("Destination length must not be set.");
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => _bytes.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeWrite?.Invoke(); WriteEntered.TrySetResult(true);
            if (holdWrite) await ReleaseWrite.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _bytes.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) _bytes.Dispose(); base.Dispose(disposing); }
    }
}
