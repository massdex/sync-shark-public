using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

internal static partial class DropboxReplyShapes
{
    // One attempt's unpublished spool; each entries occurrence replaces the records and semantic failure before it.
    private sealed class PageSpool(PairConfig pair)
    {
        private Stream? _spool;
        private BinaryWriter? _writer;
        private Exception? _failure;

        internal void Begin(Stream? spool)
        {
            ArgumentNullException.ThrowIfNull(spool);
            // Custody moves only once the writer exists: a failed Begin leaves the spool with the exchange alone.
            _writer = new BinaryWriter(spool, Encoding.UTF8, leaveOpen: true);
            _spool = spool;
            _failure = null;
        }

        internal void Reset()
        {
            DropboxPage.ResetEntries(_writer!);
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

        internal async ValueTask<DropboxPage> SealAsync(JsonElement root, CancellationToken ct)
        {
            var cursor = DropboxEntries.RequiredString(root, DropboxListing.CursorName);
            var hasMore = root.GetProperty(DropboxListing.HasMoreName).GetBoolean();
            if (root.GetProperty(DropboxListing.EntriesName).ValueKind != JsonValueKind.Array) throw new JsonException();
            if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
            _writer!.Dispose();
            _writer = null;
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
                var spool = _spool;
                _spool = null;
                if (spool is not null) await spool.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}