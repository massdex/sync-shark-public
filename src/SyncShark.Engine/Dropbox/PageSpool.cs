using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

// One attempt's unpublished spool; each entries occurrence replaces the records and semantic failure before it.
// Each decoder drain writes the records it produced; the FileStream handles buffering.
internal sealed class PageSpool(PairConfig pair)
{
    private Stream? _spool;
    private MemoryStream? _pending;
    private BinaryWriter? _writer;
    private Exception? _failure;

    internal void Begin()
    {
        _spool = DropboxReplyShapes.OpenSpool();
        _pending = new MemoryStream();
        _writer = new BinaryWriter(_pending, Encoding.UTF8, leaveOpen: true);
        _failure = null;
    }

    internal void Reset()
    {
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
        catch (Exception error) when (DropboxReplyShapes.IsItemFailure(error))
        {
            _failure = error;
            return;
        }
        DropboxPage.WriteEntry(_writer!, listed);
    }

    internal async ValueTask DrainAsync(CancellationToken ct)
    {
        _writer!.Flush();
        var pending = _pending!;
        if (pending.Length == 0) return;
        await _spool!.WriteAsync(pending.GetBuffer().AsMemory(0, checked((int)pending.Length)), ct).ConfigureAwait(false);
        pending.SetLength(0);
    }

    internal async ValueTask<DropboxPage> SealAsync(JsonElement root, CancellationToken ct)
    {
        var cursor = DropboxEntries.RequiredString(root, "cursor");
        var hasMore = root.GetProperty("has_more").GetBoolean();
        if (root.GetProperty("entries").ValueKind != JsonValueKind.Array) throw new JsonException();
        if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
        await DrainAsync(ct).ConfigureAwait(false);
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
}
