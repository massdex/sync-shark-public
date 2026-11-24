using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>An attempt-local decoder; only validated response completion transfers its typed result.</summary>
/// <remarks>This one token walk keeps the winning selected values of every closed shape, so shape rules run once on
/// winners at Complete.</remarks>
public sealed partial class DropboxReply<T>
{
    private readonly DropboxReplyShapes.Selection? _root;
    private readonly Func<JsonElement, CancellationToken, ValueTask<T>> _complete;
    private readonly Action? _begin;
    private readonly Func<ValueTask>? _abort;
    private readonly Func<CancellationToken, ValueTask>? _drain;
    private readonly List<ReplyFrame> _frames = [];
    private ReplyTokens? _tokens;
    private bool _receivedBytes;

    internal DropboxReply(DropboxReplyShapes.Selection? root, Func<JsonElement, CancellationToken, ValueTask<T>> complete,
        Action? begin = null, Func<ValueTask>? abort = null, Func<CancellationToken, ValueTask>? drain = null)
    {
        _root = root;
        _complete = complete;
        _begin = begin;
        _abort = abort;
        _drain = drain;
    }

    /// <summary>Starts one unpublished decoding attempt.</summary>
    internal partial ValueTask Begin(CancellationToken ct);

    /// <summary>Consumes a response chunk without publishing provisional values or retaining the input memory.</summary>
    internal partial ValueTask Read(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Validates EOF and winning fields before transferring the result and any sealed spool custody.</summary>
    internal partial ValueTask<T> Complete(CancellationToken ct);

    /// <summary>Discards unpublished decoding state and disposes any spool still owned by the attempt.</summary>
    internal partial ValueTask Abort();

    internal partial ValueTask Begin(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Release();
        _receivedBytes = false;
        _tokens = new ReplyTokens();
        _frames.Add(new ReplyFrame(DropboxReplyShapes.Value(_root)));
        _begin?.Invoke();
        return ValueTask.CompletedTask;
    }

    internal partial ValueTask Read(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        _receivedBytes |= !bytes.IsEmpty;
        var rest = bytes.Span;
        while (true)
        {
            var (consumed, ready) = _tokens!.Read(rest, false, Names, Retain, ct);
            rest = rest[consumed..];
            // A spooling reply writes what this chunk produced before the next read.
            if (!ready) return _drain?.Invoke(ct) ?? ValueTask.CompletedTask;
            Accept(_tokens.TokenType, _tokens.KnownNameIndex, _tokens.TakeScalar());
        }
    }

    internal partial async ValueTask<T> Complete(CancellationToken ct)
    {
        // Only Void selects no root; an empty acknowledgement has no JSON token to finish.
        if (_root is not null || _receivedBytes) Drain(ct);
        var values = _frames[0].Items;
        using var document = values is { Count: > 0 } ? JsonDocument.Parse(values[0]) : null;
        T result;
        try
        {
            result = await _complete(document?.RootElement ?? default, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (DropboxEntries.IsShapeFailure(error))
        {
            // The decoder's shape wrapper carries the extraction exception and the platform's own message.
            throw new JsonException(null, error);
        }
        Release();

        return result;
    }

    internal partial async ValueTask Abort()
    {
        Release();
        if (_abort is { } abort) await abort().ConfigureAwait(false);
    }

    private void Release()
    {
        foreach (var frame in _frames) frame.Projection?.Dispose();
        _frames.Clear();
        _tokens?.Dispose();
        _tokens = null;
    }

    private void Drain(CancellationToken ct)
    {
        var tokens = _tokens!;
        while (true)
        {
            var (_, ready) = tokens.Read(ReadOnlySpan<byte>.Empty, true, Names, Retain, ct);
            if (!ready) return;
            Accept(tokens.TokenType, tokens.KnownNameIndex, tokens.TakeScalar());
        }
    }

    // Fixed before a token starts and unchanged through its chunks, because frames change only when a token is ready.
    private ReadOnlySpan<string> Names
    {
        get
        {
            var top = _frames[^1];
            if (top.Selection is null) return default;
            if (top.Projection is not null) return top.Depth == 1 ? top.Selection.Names : top.Projection.Names;
            return top.Slots is null ? default(ReadOnlySpan<string>) : top.Selection.Names;
        }
    }

    private bool Retain
    {
        get
        {
            var top = _frames[^1];
            if (top.Selection is null) return false;
            if (top.Projection is not null) return top.Field is not null || top.Projection.Retain;
            return Next(top) is not null;
        }
    }

    private static DropboxReplyShapes.Selection? Next(ReplyFrame frame)
    {
        if (frame.Slots is not null) return frame.Field is { } field ? frame.Selection!.Fields[field] : null;
        var selection = frame.Selection!;
        var wanted = selection.Sink is not null ||
            (selection.Single ? frame.Count == 0 : frame.Count < selection.Limit);
        return wanted ? selection.Items : null;
    }

    private void Accept(JsonTokenType token, int known, byte[]? scalar)
    {
        var top = _frames[^1];
        if (top.Selection is null)
        {
            Skip(top, token);
        }
        else if (top.Projection is not null && top.Field is null)
        {
            AcceptItem(top, token, known, scalar);
        }
        else if (token == JsonTokenType.PropertyName)
        {
            top.Field = known < 0 ? null : known;
        }
        else if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
        {
            _frames.RemoveAt(_frames.Count - 1);
            ReplyFrame.Deliver(_frames[^1], top.Slots is null ? ReplyFrame.Array(top) : ReplyFrame.Object(top.Selection.Names, top.Slots));
        }
        else
        {
            Open(top, token, scalar);
        }
    }

    private void Skip(ReplyFrame top, JsonTokenType token)
    {
        // Depth counts containers nested inside this skipped one.
        if (token is JsonTokenType.StartObject or JsonTokenType.StartArray) top.Depth++;
        else if (token is not (JsonTokenType.EndObject or JsonTokenType.EndArray)) return;
        else if (top.Depth == 0) _frames.RemoveAt(_frames.Count - 1);
        else top.Depth--;
    }

    private void Open(ReplyFrame top, JsonTokenType token, byte[]? scalar)
    {
        var selection = Next(top);
        var container = token is JsonTokenType.StartObject or JsonTokenType.StartArray;
        if (selection is null)
        {
            top.Excess = top.Slots is null;
            if (container) _frames.Add(new ReplyFrame(null));
            return;
        }
        if (top.Slots is null && top.Selection!.Sink is null) top.Count++;
        selection.Starts?.Invoke();
        if (selection.Metadata)
        {
            var item = new ReplyFrame(selection) { Projection = new(), Slots = new byte[selection.Names.Length][] };
            _frames.Add(item);
            AcceptItem(item, token, default, scalar);
        }
        else if (token == JsonTokenType.StartObject && selection.Names.Length != 0)
        {
            _frames.Add(new ReplyFrame(selection) { Slots = new byte[selection.Names.Length][] });
        }
        else if (token == JsonTokenType.StartArray && selection.Items is not null)
        {
            _frames.Add(new ReplyFrame(selection));
        }
        else if (container)
        {
            // A wrong-kind selected container keeps its kind only, and its descendants are never captured.
            ReplyFrame.Deliver(top, token == JsonTokenType.StartObject ? ReplyFrame.Object([], []) : ReplyFrame.Array(new ReplyFrame(null)));
            _frames.Add(new ReplyFrame(null));
        }
        else
        {
            ReplyFrame.Deliver(top, scalar!);
        }
    }

    private void AcceptItem(ReplyFrame item, JsonTokenType token, int known, byte[]? scalar)
    {
        var wholeItemField = token == JsonTokenType.PropertyName && item.Depth == 1;
        if (wholeItemField && known >= DropboxEntries.Projection.Fields.Length)
        {
            item.Field = known;
            return;
        }
        item.Projection!.Accept(token, known, scalar);
        if (token is JsonTokenType.StartObject or JsonTokenType.StartArray) item.Depth++;
        else if (token is JsonTokenType.EndObject or JsonTokenType.EndArray) item.Depth--;
        if (item.Depth == 0) CloseItem(item);
    }

    private void CloseItem(ReplyFrame item)
    {
        _frames.RemoveAt(_frames.Count - 1);
        var parent = _frames[^1];
        using var document = item.Projection!.Complete();
        item.Projection.Dispose();
        if (parent.Selection?.Sink is { } sink)
        {
            sink(document.RootElement);
            return;
        }
        if (item.Selection!.Names.Length == DropboxEntries.Projection.Fields.Length || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            ReplyFrame.Deliver(parent, Encoding.UTF8.GetBytes(document.RootElement.GetRawText()));
            return;
        }
        // A flattened finish-batch entry keeps its metadata and the sibling failure union in wire shape.
        ReplyFrame.Deliver(parent, ReplyFrame.Write(writer =>
        {
            writer.WriteStartObject();
            foreach (var field in document.RootElement.EnumerateObject()) field.WriteTo(writer);
            for (int i = DropboxEntries.Projection.Fields.Length; i < item.Selection.Names.Length; i++)
                if (item.Slots![i] is { } extra)
                {
                    writer.WritePropertyName(item.Selection.Names[i]);
                    writer.WriteRawValue(extra, skipInputValidation: true);
                }
            writer.WriteEndObject();
        }));
    }
}

/// <summary>Consumes the complete response before publishing a typed result.</summary>
public static class DropboxReply
{
    // Transfer chunk, not a receive-size limit; the 0.3.95 streaming chunk SyncContentIdentity.DefaultReadBufferBytes retains.
    internal const int ChunkBytes = 128 * 1024;

    /// <summary>Consume the body inside its transport attempt; the caller owns the response stream.</summary>
    public static async Task<T> ReadAsync<T>(Stream body, DropboxReply<T> decoder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await decoder.Begin(ct).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            try
            {
                int size;
                while ((size = await body.ReadAsync(buffer.AsMemory(0, ChunkBytes), ct).ConfigureAwait(false)) != 0)
                    await decoder.Read(buffer.AsMemory(0, size), ct).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            ct.ThrowIfCancellationRequested();
            return await decoder.Complete(ct).ConfigureAwait(false);
        }
        finally
        {
            await decoder.Abort().ConfigureAwait(false);
        }
    }
}
