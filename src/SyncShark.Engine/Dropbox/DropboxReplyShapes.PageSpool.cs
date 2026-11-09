using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

internal static partial class DropboxReplyShapes
{
    // One attempt's unpublished spool; each entries occurrence replaces the records and semantic failure before it.
    // Entries encode once into a pending buffer; only whole chunks, then the final tail, are written to the spool,
    // each by one awaited write the spool admits against free space (#36). An unwritten tail is never written on abort.
    private sealed class PageSpool(PairConfig pair)
    {
        private static readonly int Chunk = SyncContentIdentity.DefaultReadBufferBytes;
        private Stream? _spool;
        private MemoryStream? _pending;
        private BinaryWriter? _writer;
        private Exception? _failure;

        internal void Begin(Stream? spool)
        {
            ArgumentNullException.ThrowIfNull(spool);
            // Custody moves only once the writer exists: a failed Begin leaves the spool with the exchange alone.
            _pending = new MemoryStream();
            _writer = new BinaryWriter(_pending, Encoding.UTF8, leaveOpen: true);
            _spool = spool;
            _failure = null;
        }

        internal void Reset()
        {
            // The old generation's unwritten tail goes first, then its written chunks.
            _writer!.Flush();
            _pending!.SetLength(0);
            _spool!.SetLength(0);
            _spool.Position = 0;
            _failure = null;
        }

        internal void Add(JsonElement metadata)
        {
            if (_failure is not null) return;
            Listed listed;
            try
            {
                listed = DropboxEntries.Parse(metadata, pair);
            }
            catch (Exception error) when (IsItemFailure(error))
            {
                _failure = error;
                return;
            }
            DropboxPage.WriteEntry(_writer!, listed);
        }

        /// <summary>Writes every whole pending chunk; the remainder waits for more entries or the seal.</summary>
        internal ValueTask DrainAsync(CancellationToken ct) => WriteAsync(tail: false, ct);

        internal async ValueTask<DropboxPage> SealAsync(JsonElement root, CancellationToken ct)
        {
            var cursor = DropboxEntries.RequiredString(root, DropboxListing.CursorName);
            var hasMore = root.GetProperty(DropboxListing.HasMoreName).GetBoolean();
            if (root.GetProperty(DropboxListing.EntriesName).ValueKind != JsonValueKind.Array) throw new JsonException();
            if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
            await WriteAsync(tail: true, ct).ConfigureAwait(false);
            _writer!.Dispose();
            _writer = null;
            _pending = null;
            var page = await DropboxPage.SealAsync(_spool!, cursor, hasMore, ct).ConfigureAwait(false);
            _spool = null;
            return page;
        }

        internal async ValueTask AbortAsync()
        {
            try
            {
                _writer?.Dispose();
            }
            finally
            {
                _writer = null;
                _pending = null;
                var spool = _spool;
                _spool = null;
                if (spool is not null) await spool.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask WriteAsync(bool tail, CancellationToken ct)
        {
            _writer!.Flush();
            var pending = _pending!;
            var length = checked((int)pending.Length);
            var written = 0;
            while (length - written >= Chunk || tail && length > written)
            {
                var count = Math.Min(Chunk, length - written);
                await _spool!.WriteAsync(pending.GetBuffer().AsMemory(written, count), ct).ConfigureAwait(false);
                written += count;
            }
            if (written == 0) return;
            // Keep only the unwritten remainder at the buffer's start.
            var rest = length - written;
            pending.GetBuffer().AsSpan(written, rest).CopyTo(pending.GetBuffer());
            pending.SetLength(rest);
        }
    }
}
