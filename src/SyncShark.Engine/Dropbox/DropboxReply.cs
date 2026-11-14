using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>An attempt-local decoder; only validated response completion transfers its typed result.</summary>
/// <remarks>This one token walk keeps the winning selected values of every closed shape, so shape rules run once on
/// winners at Complete. It stays above the 300-line prompt because its shapes share that single walk.</remarks>
public sealed partial class DropboxReply<T>
{
    private readonly DropboxReplyShapes.Selection? _root;
    private readonly Func<JsonElement, CancellationToken, ValueTask<T>> _complete;
    private readonly Action<Stream?>? _begin;
    private readonly Func<ValueTask>? _abort;
    private readonly Func<CancellationToken, ValueTask>? _drain;
    private readonly List<Frame> _frames = [];
    private DropboxEntries.ReplyTokens? _tokens;

    internal DropboxReply(DropboxReplyShapes.Selection? root, Func<JsonElement, CancellationToken, ValueTask<T>> complete,
        Action<Stream?>? begin = null, Func<ValueTask>? abort = null, Func<CancellationToken, ValueTask>? drain = null)
    {
        _root = root;
        _complete = complete;
        _begin = begin;
        _abort = abort;
        _drain = drain;
    }

    /// <summary>Receives a validated result at decode, for a body fact that must not wait for transport cleanup.</summary>
    internal Action<T>? Accepted { get; set; }

    /// <summary>Begins unpublished decoding with the API-owned page spool, or null for other replies.</summary>
    internal partial ValueTask Begin(Stream? spool, CancellationToken ct);

    /// <summary>Consumes a response chunk without publishing provisional values or retaining the input memory.</summary>
    internal partial ValueTask Read(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Validates EOF and winning fields before transferring the result and any sealed spool custody.</summary>
    internal partial ValueTask<T> Complete(CancellationToken ct);

    /// <summary>Discards unpublished decoding state and disposes any spool still owned by the attempt.</summary>
    internal partial ValueTask Abort();

    internal partial ValueTask Begin(Stream? spool, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Release();
        _tokens = new DropboxEntries.ReplyTokens();
        _frames.Add(new Frame(DropboxReplyShapes.Value(_root)));
        _begin?.Invoke(spool);
        return ValueTask.CompletedTask;
    }

    internal partial ValueTask Read(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
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
        Drain(ct);
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
        Accepted?.Invoke(result);
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
            if (top.Projection is not null) return top.Depth == 1 ? top.Selection.Names : default(ReadOnlySpan<string>);
            return top.Slots is null ? default(ReadOnlySpan<string>) : top.Selection.Names;
        }
    }

    private bool Retain
    {
        get
        {
            var top = _frames[^1];
            if (top.Selection is null) return false;
            if (top.Projection is not null) return top.Field is not null || (top.Depth == 1 && top.Pending);
            return Next(top) is not null;
        }
    }

    private static DropboxReplyShapes.Selection? Next(Frame frame)
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
            Deliver(_frames[^1], top.Slots is null ? Array(top) : Object(top.Selection.Names, top.Slots));
        }
        else
        {
            Open(top, token, scalar);
        }
    }

    private void Skip(Frame top, JsonTokenType token)
    {
        // Depth counts containers nested inside this skipped one.
        if (token is JsonTokenType.StartObject or JsonTokenType.StartArray) top.Depth++;
        else if (token is not (JsonTokenType.EndObject or JsonTokenType.EndArray)) return;
        else if (top.Depth == 0) _frames.RemoveAt(_frames.Count - 1);
        else top.Depth--;
    }

    private void Open(Frame top, JsonTokenType token, byte[]? scalar)
    {
        var selection = Next(top);
        var container = token is JsonTokenType.StartObject or JsonTokenType.StartArray;
        if (selection is null)
        {
            top.Excess = top.Slots is null;
            if (container) _frames.Add(new Frame(null));
            return;
        }
        if (top.Slots is null && top.Selection!.Sink is null) top.Count++;
        selection.Starts?.Invoke();
        if (selection.Metadata)
        {
            var item = new Frame(selection) { Projection = new(), Slots = new byte[selection.Names.Length][] };
            _frames.Add(item);
            AcceptItem(item, token, default, scalar);
        }
        else if (token == JsonTokenType.StartObject && selection.Names.Length != 0)
        {
            _frames.Add(new Frame(selection) { Slots = new byte[selection.Names.Length][] });
        }
        else if (token == JsonTokenType.StartArray && selection.Items is not null)
        {
            _frames.Add(new Frame(selection));
        }
        else if (container)
        {
            // A wrong-kind selected container keeps its kind only, and its descendants are never captured.
            Deliver(top, token == JsonTokenType.StartObject ? Object([], []) : Array(new Frame(null)));
            _frames.Add(new Frame(null));
        }
        else
        {
            Deliver(top, scalar!);
        }
    }

    private void AcceptItem(Frame item, JsonTokenType token, int known, byte[]? scalar)
    {
        var wholeItemField = token == JsonTokenType.PropertyName && item.Depth == 1;
        if (wholeItemField && known >= DropboxEntries.Projection.Fields.Length)
        {
            item.Pending = false;
            item.Field = known;
            return;
        }
        item.Pending = wholeItemField && known >= 0;
        item.Projection!.Accept(token, known, scalar);
        if (token is JsonTokenType.StartObject or JsonTokenType.StartArray) item.Depth++;
        else if (token is JsonTokenType.EndObject or JsonTokenType.EndArray) item.Depth--;
        if (item.Depth == 0) CloseItem(item);
    }

    private void CloseItem(Frame item)
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
        var body = Encoding.UTF8.GetBytes(document.RootElement.GetRawText());
        if (item.Selection!.Names.Length == DropboxEntries.Projection.Fields.Length)
        {
            Deliver(parent, body);
            return;
        }
        // Extra member fields sit beside the projected metadata, which keeps its own field names.
        item.Slots![0] = body;
        var names = item.Selection.Names.ToArray();
        names[0] = DropboxWriter.MetadataName;
        Deliver(parent, Object(names, item.Slots));
    }

    private static void Deliver(Frame frame, byte[] value)
    {
        if (frame.Slots is not null)
        {
            frame.Slots[frame.Field!.Value] = value;
            frame.Field = null;
        }
        else
        {
            (frame.Items ??= []).Add(value);
        }
    }

    private static byte[] Object(string[] names, byte[]?[] slots) => Write(writer =>
    {
        writer.WriteStartObject();
        for (int index = 0; index < names.Length; index++)
        {
            if (slots[index] is not { } value) continue;
            writer.WritePropertyName(names[index]);
            writer.WriteRawValue(value, skipInputValidation: true);
        }
        writer.WriteEndObject();
    });

    private static byte[] Array(Frame frame) => Write(writer =>
    {
        writer.WriteStartArray();
        foreach (var value in frame.Items ?? []) writer.WriteRawValue(value, skipInputValidation: true);
        // Excess elements are never captured; one null keeps the array length from matching a bounded count.
        if (frame.Excess) writer.WriteNullValue();
        writer.WriteEndArray();
    });

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) body(writer);
        return output.ToArray();
    }

    // Selection null is a skipped container; Slots marks an object or item, otherwise it is an array.
    private sealed class Frame(DropboxReplyShapes.Selection? selection)
    {
        internal readonly DropboxReplyShapes.Selection? Selection = selection;
        internal byte[]?[]? Slots;
        internal List<byte[]>? Items;
        internal DropboxEntries.Projection? Projection;
        internal int? Field;
        internal int Count;
        internal int Depth;
        internal bool Pending;
        internal bool Excess;
    }
}
