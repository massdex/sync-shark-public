using System.Security.Cryptography;

namespace Massdex.SyncShark;

// One instance per account/root-bound provider client. This shares only in-flight exact-content
// cohorts: the last range lease cancels/releases the cohort, with no persistent or timed cache.
internal sealed class SyncVerifiedRangeDownload
{
    private const long SafetyReserve = 64L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<Identity, Entry> _entries = [];
    internal string? StagingRootForTest { get; set; }
    internal int ActiveCohortsForTest { get { lock (_gate) return _entries.Count; } }

    internal async Task<SyncRangeDownloadResult> DownloadAsync(PilotRemoteEntry expected, long offset,
        long length, Stream destination,
        Func<PilotRemoteEntry, Stream, CancellationToken, Task<SyncDownloadResult>> download,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(download);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanWrite) throw new IOException("The range destination must be writable.");
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || expected.ContentHash is null ||
            expected.ContentHash.Trim().Length != 64 || !expected.ContentHash.Trim().All(Uri.IsHexDigit))
            throw new InvalidDataException("Verified Google ranges require an exact binary id, revision and SHA-256.");
        if (expected.Size < 0 || offset < 0 || length <= 0 || offset > expected.Size ||
            length > expected.Size - offset || expected.Size > long.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(length), "The range or its staging reservation exceeds the reviewed object boundary.");

        var key = new Identity(expected.RelativePath, expected.Id, expected.Revision, expected.Size,
            expected.ContentHash.Trim().ToLowerInvariant());
        Entry entry;
        bool start;
        lock (_gate)
        {
            start = !_entries.TryGetValue(key, out entry!) || entry.Ready.Task.IsFaulted || entry.Ready.Task.IsCanceled;
            if (start) _entries[key] = entry = new Entry();
            entry.Waiters++;
        }
        if (start) _ = PopulateAsync(entry, expected, download);
        try
        {
            var stage = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await stage.CopyAsync(offset, length, destination, cancellationToken).ConfigureAwait(false);
            return new(stage.Result.FileId, stage.Result.Revision, stage.Result.Size, stage.Result.ContentHash, offset, length);
        }
        finally { await ReleaseAsync(key, entry).ConfigureAwait(false); }
    }

    private async Task PopulateAsync(Entry entry, PilotRemoteEntry expected,
        Func<PilotRemoteEntry, Stream, CancellationToken, Task<SyncDownloadResult>> download)
    {
        try { entry.Ready.TrySetResult(await StageAsync(expected, download, entry.Cancellation.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException error) { entry.Ready.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { entry.Ready.TrySetException(error); }
    }

    private async Task ReleaseAsync(Identity key, Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Waiters != 0) return;
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
        }
        try
        {
            try { entry.Cancellation.Cancel(); }
            finally
            {
                Stage? stage = null;
                try { stage = await entry.Ready.Task.ConfigureAwait(false); }
                catch (Exception) { /* Download failure is delivered to each waiter; also observe it during cleanup. */ }
                if (stage is not null) await stage.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { entry.Cancellation.Dispose(); }
    }

    private async Task<Stage> StageAsync(PilotRemoteEntry expected,
        Func<PilotRemoteEntry, Stream, CancellationToken, Task<SyncDownloadResult>> download,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(StagingRootForTest ?? SyncSharkRuntimePaths.ReplacementStagingRoot);
        var parent = Path.GetDirectoryName(root) ?? throw new IOException("The staging directory has no bounded parent.");
        // Hold the existing parent and atomically open/create only this registered staging child.
        // Opening a lease verifies the actual NTFS identity/final path; do not follow reparse roots.
        if (PilotPath.IsUnsafeAttributes(File.GetAttributes(parent)))
            throw new IOException("The staging parent must be an on-disk directory, not a reparse point.");
        using var parentLease = PilotDirectoryLease.OpenProductDirectory(parent);
        PilotDirectoryLease? rootLease = null;
        SyncRecoverySpoolAdmission.Reservation? reservation = null;
        FileStream? stream = null;
        try
        {
            rootLease = PilotDirectoryLease.OpenOrCreateDescendant(parentLease, Path.GetFileName(root));
            var volume = Path.GetPathRoot(root) ?? throw new IOException("The staging directory has no volume.");
            reservation = await SyncRecoverySpoolAdmission.ReserveAsync(volume, checked(expected.Size * 2),
                Guid.NewGuid().ToString("N"), new DriveInfo(volume).AvailableFreeSpace, SafetyReserve,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            rootLease.VerifyBinding();
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            rootLease.VerifyBinding();
            if (!string.Equals(PilotDirectoryLease.FinalPath(stream.SafeFileHandle), path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The private staging path changed during creation.");
            // The provider's current metadata must not expand a reservation made for the reviewed
            // size. Keep the real seekable-download contract, but cap all stage growth at that size.
            using var bounded = new BoundedStageStream(stream, expected.Size);
            var result = await download(expected, bounded, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.FileId != expected.Id || result.Revision != expected.Revision || result.Size != expected.Size ||
                string.IsNullOrWhiteSpace(result.ContentHash) ||
                !string.Equals(result.ContentHash.Trim(), expected.ContentHash!.Trim(), StringComparison.OrdinalIgnoreCase) ||
                stream.Length != expected.Size)
                throw new IOException("The staged download differs from the exact reviewed Google identity, revision, size or hash.");
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(digest, expected.ContentHash!.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged Google bytes do not match the reviewed SHA-256.");
            cancellationToken.ThrowIfCancellationRequested();
            rootLease.VerifyBinding();
            parentLease.VerifyBinding();
            var stage = new Stage(stream, rootLease, reservation, result with { ContentHash = digest });
            stream = null; rootLease = null; reservation = null;
            return stage;
        }
        finally
        {
            try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            finally { try { rootLease?.Dispose(); } finally { reservation?.Dispose(); } }
        }
    }

    private sealed record Identity(string Path, string FileId, string Revision, long Size, string Hash);
    private sealed class Entry
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<Stage> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
    }

    private sealed class BoundedStageStream(Stream inner, long limit) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set { RequirePosition(value); inner.Position = value; }
        }
        private void RequirePosition(long value)
        { if (value < 0 || value > limit) throw new IOException("The Google download exceeds its reviewed staging reservation."); }
        private void RequireWrite(int count)
        {
            var position = inner.Position;
            RequirePosition(position);
            if (count < 0 || count > limit - position)
                throw new IOException("The Google download exceeds its reviewed staging reservation.");
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var start = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => inner.Position,
                SeekOrigin.End => inner.Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            var position = checked(start + offset); RequirePosition(position); return inner.Seek(position, SeekOrigin.Begin);
        }
        public override void SetLength(long value) { RequirePosition(value); inner.SetLength(value); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { RequireWrite(count); inner.Write(buffer, offset, count); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); RequireWrite(count); return inner.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); RequireWrite(buffer.Length); return inner.WriteAsync(buffer, cancellationToken); }
        // Leave the private file open: its cohort owns DeleteOnClose and the lifetime reservation.
    }

    private sealed class Stage(FileStream stream, PilotDirectoryLease root,
        SyncRecoverySpoolAdmission.Reservation reservation, SyncDownloadResult result) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _copyGate = new(1, 1);
        internal SyncDownloadResult Result { get; } = result;
        internal async Task CopyAsync(long offset, long length, Stream destination, CancellationToken cancellationToken)
        {
            await _copyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.VerifyBinding();
                if (stream.Length != Result.Size) throw new IOException("The verified staging length changed.");
                stream.Position = offset; // Never read or change the final destination's Position.
                var buffer = new byte[(int)Math.Min(128 * 1024, length)];
                var remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new IOException("The verified stage ended before the requested range.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            finally { _copyGate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { root.Dispose(); }
                finally { try { reservation.Dispose(); } finally { _copyGate.Dispose(); } }
            }
        }
    }
}
