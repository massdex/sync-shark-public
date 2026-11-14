using System.Text;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>A sealed, attempt-owned local spool with one ordered replay; never a durable checkpoint.</summary>
public sealed class DropboxPage : IAsyncDisposable
{
    private const int ReadStarted = 0 + 1;

    private Stream? _spool;
    private readonly long _length;
    private readonly CancellationToken _cancellation;
    private int _readStarted;

    private DropboxPage(Stream spool, string cursor, bool hasMore, CancellationToken cancellation)
    {
        _spool = spool;
        _length = spool.Length;
        _cancellation = cancellation;
        Cursor = cursor;
        HasMore = hasMore;
    }

    public string Cursor { get; }
    public bool HasMore { get; }

    /// <summary>Transfers custody after the decoder validates EOF and closes its leave-open writer.
    /// On failure the attempt still owns and must dispose the stream.</summary>
    internal static async Task<DropboxPage> SealAsync(Stream spool, string cursor, bool hasMore,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(cursor);
        ct.ThrowIfCancellationRequested();
        if (!spool.CanRead || !spool.CanWrite || !spool.CanSeek)
        {
            throw new ArgumentException(null, nameof(spool));
        }
        await spool.FlushAsync(ct).ConfigureAwait(false);
        spool.Position = 0;
        ct.ThrowIfCancellationRequested();
        return new DropboxPage(spool, cursor, hasMore, ct);
    }

    /// <summary>Writes one already-parsed item to the attempt's leave-open binary writer.</summary>
    internal static void WriteEntry(BinaryWriter writer, Listed listed)
    {
        writer.Write(listed.Path);
        writer.Write(listed.Entry is not null);
        if (listed.Entry is not { } entry) return;
        writer.Write(entry.Path);
        writer.Write(entry.IsFolder);
        writer.Write(entry.Size);
        writer.Write(entry.Modified.Ticks);
        writer.Write(entry.Modified.Offset.Ticks);
        writer.Write(entry.Id);
        writer.Write(entry.Revision);
        writer.Write(entry.ContentHash);
        writer.Write(entry.Skip is not null);
        if (entry.Skip is { } skip) writer.Write(skip);
    }

    /// <summary>Replays once synchronously, including duplicate events, with no retry after any failure.</summary>
    public IEnumerable<Listed> ReadEntries()
    {
        var spool = _spool ?? throw new ObjectDisposedException(nameof(DropboxPage));
        if (Interlocked.Exchange(ref _readStarted, ReadStarted) != 0)
        {
            throw new InvalidOperationException();
        }
        using var reader = new BinaryReader(spool, Encoding.UTF8, leaveOpen: true);
        while (spool.Position < _length)
        {
            _cancellation.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_spool is null, this);
            var path = reader.ReadString();
            Entry? entry = null;
            if (reader.ReadBoolean())
            {
                entry = new Entry(reader.ReadString(), reader.ReadBoolean(), reader.ReadInt64(),
                    new DateTimeOffset(reader.ReadInt64(), new TimeSpan(reader.ReadInt64())),
                    reader.ReadString(), reader.ReadString(), reader.ReadString(),
                    reader.ReadBoolean() ? reader.ReadString() : null);
            }
            if (spool.Position > _length) throw new EndOfStreamException();
            yield return new Listed(path, entry);
        }
        _cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_spool is null, this);
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _spool, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
}
