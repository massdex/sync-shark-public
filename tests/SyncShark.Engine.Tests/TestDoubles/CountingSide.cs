using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>The budget and scenario ISide counter. It keeps their different meanings apart: <see cref="Reads"/>
/// counts ReadOne metadata, while <see cref="ContentReads"/> is content opens plus hashes and excludes ReadOne.
/// Fault injection and other decorator behaviour stay with their consumers.</summary>
internal sealed class CountingSide(ISide inner) : ISide
{
    private int _listings;
    private int _subtreeListings;
    private int _siblingListings;
    private long _yielded;
    private int _reads;
    private int _opens;
    private int _disposals;
    private long _bytesRead;
    private int _hashes;
    private long _hashBytes;
    private int _stages;
    private int _publishes;
    private int _ensures;
    private int _deletes;
    private int _moves;

    public Side Side => inner.Side;

    /// <summary>ReadAllAsync calls; a root listing and each subtree listing count separately.</summary>
    public int Listings => Volatile.Read(ref _listings);
    public int SubtreeListings => Volatile.Read(ref _subtreeListings);
    public int SiblingListings => Volatile.Read(ref _siblingListings);

    /// <summary>Listing events actually yielded to the consumer.</summary>
    public long Yielded => Volatile.Read(ref _yielded);

    /// <summary>ReadOne metadata calls (the budget check's Reads meaning).</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Content opens.</summary>
    public int Opens => Volatile.Read(ref _opens);

    /// <summary>Opened content streams disposed by their caller.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    /// <summary>Bytes read through the opened content streams.</summary>
    public long BytesRead => Volatile.Read(ref _bytesRead);

    public int Hashes => Volatile.Read(ref _hashes);

    /// <summary>The recorded size of every hashed entry; ContentHashAsync hands over no stream, so this is the
    /// entry's stated size, not an observed read.</summary>
    public long HashBytes => Volatile.Read(ref _hashBytes);

    public int Stages => Volatile.Read(ref _stages);
    public int Publishes => Volatile.Read(ref _publishes);
    public int Ensures => Volatile.Read(ref _ensures);
    public int Deletes => Volatile.Read(ref _deletes);
    public int Moves => Volatile.Read(ref _moves);

    /// <summary>Content opens plus hashes, excluding ReadOne (the scenario check's LocalReads meaning).</summary>
    public int ContentReads => Opens + Hashes;

    /// <summary>Every call that would copy, create or delete content.</summary>
    public int Work => Opens + Stages + Publishes + Ensures + Deletes + Moves;

    public void Reset()
    {
        _listings = _subtreeListings = _siblingListings = 0;
        _yielded = 0;
        _reads = _opens = _disposals = 0;
        _bytesRead = 0;
        _hashes = 0;
        _hashBytes = 0;
        _stages = _publishes = _ensures = _deletes = _moves = 0;
    }

    public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _listings);
        if (!options.Recursive) Interlocked.Increment(ref _siblingListings);
        else if (under.Length != 0) Interlocked.Increment(ref _subtreeListings);
        await foreach (var listed in inner.ReadAllAsync(under, options, ct))
        {
            Interlocked.Increment(ref _yielded);
            yield return listed;
        }
    }

    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadOneAsync(path, ct);
    }

    public async Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
    {
        Interlocked.Increment(ref _opens);
        return new CountedStream(await inner.OpenReadAsync(entry, attempt, ct).ConfigureAwait(false), this);
    }

    public async Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
    {
        Interlocked.Increment(ref _hashes);
        Interlocked.Add(ref _hashBytes, entry.Size);
        return await inner.ContentHashAsync(entry, ct).ConfigureAwait(false);
    }

    public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
    {
        Interlocked.Increment(ref _stages);
        return inner.StageAsync(path, content, source, ct);
    }

    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _publishes);
        return inner.PublishAsync(staged, expected, selection, ct);
    }

    public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _ensures);
        return inner.EnsureFolderAsync(path, ct);
    }

    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        Interlocked.Increment(ref _deletes);
        return inner.DeleteAsync(expected, selection, ct);
    }

    public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        Interlocked.Increment(ref _moves);
        return inner.MoveAsync(expected, newPath, ct);
    }

    public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
        inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);

    public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct) =>
        inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);

    private void CountRead(int bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _bytesRead, bytes);
    }

    private void CountDisposal() => Interlocked.Increment(ref _disposals);

    /// <summary>Forwards the opened stream unchanged except for counting the bytes read and the single disposal,
    /// keeping its seekability, partial-read and disposal semantics.</summary>
    private sealed class CountedStream(Stream inner, CountingSide owner) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            owner.CountRead(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            owner.CountRead(read);
            return read;
        }

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0) owner.CountRead(1);
            return value;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
            owner.CountRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            owner.CountRead(read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.CountDisposal();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.CountDisposal();
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
