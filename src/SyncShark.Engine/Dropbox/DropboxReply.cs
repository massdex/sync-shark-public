using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>An attempt-local decoder; only validated response completion transfers its typed result.</summary>
/// <remarks>This one token walk keeps the winning selected values of every closed shape, so shape rules run once on
/// winners at Complete. It stays above the 300-line prompt because its shapes share that single walk.</remarks>
public sealed partial class DropboxReply<T>
{
    private readonly DropboxReply.Selection? _root;
    private readonly Func<JsonElement, CancellationToken, ValueTask<T>> _complete;
    private readonly Action<Stream?>? _begin;
    private readonly Func<ValueTask>? _abort;
    private readonly List<Frame> _frames = [];
    private DropboxEntries.ReplyTokens? _tokens;

    internal DropboxReply(DropboxReply.Selection? root, Func<JsonElement, CancellationToken, ValueTask<T>> complete,
        Action<Stream?>? begin = null, Func<ValueTask>? abort = null)
    {
        _root = root;
        _complete = complete;
        _begin = begin;
        _abort = abort;
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
        _frames.Add(new Frame(DropboxReply.Value(_root)));
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
            if (!ready) return ValueTask.CompletedTask;
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

    private static DropboxReply.Selection? Next(Frame frame)
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
    private sealed class Frame(DropboxReply.Selection? selection)
    {
        internal readonly DropboxReply.Selection? Selection = selection;
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

/// <summary>The closed reply shapes consumed by the Dropbox adapter, not an extensible decoder registry.</summary>
internal static partial class DropboxReply
{
    private static readonly Selection Scalar = new();
    private static readonly Selection MetadataValue = new()
    {
        Metadata = true,
        Names = DropboxEntries.Projection.Fields,
        Fields = new Selection?[DropboxEntries.Projection.Fields.Length]
    };
    private static readonly Selection Failure = FailureUnion();
    private static readonly Selection UploadMember = UploadMemberValue();
    private static readonly Selection FolderMember = Object((DropboxProtocol.Tag, Scalar),
        (DropboxWriter.MetadataName, MetadataValue), (DropboxProtocol.TagFailure, Failure));
    private static readonly Selection Head = Object((DropboxEntries.IdName, Scalar), (DropboxEntries.RevName, Scalar),
        (DropboxEntries.PathDisplayName, Scalar), (DropboxProtocol.ServerDeleted, Scalar),
        (DropboxEntries.ContentHashName, Scalar));

    /// <summary>Which values of one JSON value are retained; every other value is validated and discarded.</summary>
    internal sealed class Selection
    {
        internal string[] Names { get; init; } = [];
        internal Selection?[] Fields { get; init; } = [];
        internal Selection? Items { get; init; }
        internal int Limit { get; init; }
        // Keeps only element zero: the response root, or a history's head.
        internal bool Single { get; init; }
        internal bool Metadata { get; init; }
        internal Action? Starts { get; init; }
        internal Action<JsonElement>? Sink { get; init; }
    }

    internal static Selection Value(Selection? root) => new() { Items = root, Single = true };

    private static Selection Object(params (string Name, Selection Value)[] fields) => new()
    {
        Names = fields.Select(field => field.Name).ToArray(),
        Fields = fields.Select(field => (Selection?)field.Value).ToArray()
    };

    // Dropbox failure unions nest one selected tag vocabulary at every level.
    private static Selection FailureUnion()
    {
        string[] names = [DropboxProtocol.Tag, DropboxProtocol.Path, DropboxErrors.ReasonName,
            DropboxProtocol.PathLookup, DropboxProtocol.PathWrite];
        var fields = new Selection?[names.Length];
        var union = new Selection { Names = names, Fields = fields };
        System.Array.Fill(fields, union);
        return union;
    }

    // A finish member flattens its metadata beside .tag; the failure is the one extra field.
    private static Selection UploadMemberValue()
    {
        var count = DropboxEntries.Projection.Fields.Length;
        string[] names = [.. DropboxEntries.Projection.Fields, DropboxProtocol.TagFailure];
        var fields = new Selection?[names.Length];
        fields[count] = Failure;
        return new Selection { Metadata = true, Names = names, Fields = fields };
    }

    private static DropboxReply<T> Of<T>(Selection? root, Func<JsonElement, T> complete) =>
        new(root, (value, _) => ValueTask.FromResult(complete(value)));

    internal static partial DropboxReply<DropboxPage> Page(PairConfig pair);

    internal static partial DropboxReply<Listed> Listed(PairConfig pair);

    internal static partial DropboxReply<Entry> Metadata(
        PairConfig pair, string? untaggedKind = null, bool nested = false);

    internal static partial DropboxReply<string> SessionId();

    internal static partial DropboxReply<ValueTuple> Void();

    internal static partial DropboxReply<string> Cursor();

    internal static partial DropboxReply<(bool Changes, TimeSpan? Backoff)> Longpoll();

    internal static partial DropboxReply<(string AccountId, string RootNamespaceId,
        string HomeNamespaceId)> AccountRoot();

    internal static partial DropboxReply<(bool IsDeleted, bool HasHead, string? ServerDeleted,
        bool TimeWrongType, string? Id, string? Revision, string? NativePath,
        string? ContentHash, bool HashWrongType)> HistoryHead();

    internal static partial DropboxReply<IReadOnlyList<(Entry? Entry, Exception? Error,
        bool KnownNoEffect, bool WriteThrottled)>> FinishBatch(PairConfig pair,
            IReadOnlyList<(string Path, bool ExpectedIsNull)> targets);

    internal static partial DropboxReply<FolderSubmitResult> FolderSubmit(PairConfig pair, int expectedCount);

    internal static partial DropboxReply<FolderJobResult> FolderJob(PairConfig pair, int expectedCount);

    internal static partial DropboxReply<DropboxPage> Page(PairConfig pair)
    {
        var page = new PageSpool(pair);
        var entries = new Selection { Items = MetadataValue, Starts = page.Reset, Sink = page.Add };
        return new(Object((DropboxListing.CursorName, Scalar), (DropboxListing.HasMoreName, Scalar),
            (DropboxListing.EntriesName, entries)), page.SealAsync, page.Begin, page.AbortAsync);
    }

    internal static partial DropboxReply<Listed> Listed(PairConfig pair) =>
        Of(MetadataValue, root => DropboxEntries.Parse(root, pair));

    internal static partial DropboxReply<Entry> Metadata(PairConfig pair, string? untaggedKind, bool nested) => nested
        ? Of(Object((DropboxWriter.MetadataName, MetadataValue)), root =>
            DropboxEntries.RequireLive(root.GetProperty(DropboxWriter.MetadataName), pair, untaggedKind))
        : Of(MetadataValue, root => DropboxEntries.RequireLive(root, pair, untaggedKind));

    internal static partial DropboxReply<string> SessionId() => RequiredText(DropboxProtocol.SessionId);

    internal static partial DropboxReply<ValueTuple> Void() => Of<ValueTuple>(null, _ => default);

    internal static partial DropboxReply<string> Cursor() => RequiredText(DropboxListing.CursorName);

    private static DropboxReply<string> RequiredText(string name) =>
        Of(Object((name, Scalar)), root => DropboxEntries.RequiredString(root, name));

    internal static partial DropboxReply<(bool Changes, TimeSpan? Backoff)> Longpoll() =>
        Of<(bool Changes, TimeSpan? Backoff)>(Object((DropboxProtocol.Changes, Scalar),
            (DropboxProtocol.Backoff, Scalar)), root =>
        {
            var changes = root.GetProperty(DropboxProtocol.Changes).GetBoolean();
            var backoff = Field(root, DropboxProtocol.Backoff);
            if (backoff.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return (changes, (TimeSpan?)null);
            var seconds = backoff.GetInt64();
            if (seconds < 0) throw new JsonException();
            return (changes, TimeSpan.FromSeconds(seconds));
        });

    internal static partial DropboxReply<(string AccountId, string RootNamespaceId,
        string HomeNamespaceId)> AccountRoot() =>
        Of(Object((DropboxProtocol.AccountId, Scalar), (DropboxProtocol.RootInfo,
            Object((DropboxProtocol.RootNamespaceId, Scalar), (DropboxProtocol.HomeNamespaceId, Scalar)))), root =>
        {
            var info = root.GetProperty(DropboxProtocol.RootInfo);
            return (NonEmpty(root, DropboxProtocol.AccountId), NonEmpty(info, DropboxProtocol.RootNamespaceId),
                NonEmpty(info, DropboxProtocol.HomeNamespaceId));
        });

    internal static partial DropboxReply<(bool IsDeleted, bool HasHead, string? ServerDeleted,
        bool TimeWrongType, string? Id, string? Revision, string? NativePath,
        string? ContentHash, bool HashWrongType)> HistoryHead() =>
        Of(Object((DropboxProtocol.IsDeleted, Scalar), (DropboxProtocol.ServerDeleted, Scalar),
            (DropboxListing.EntriesName, new Selection { Items = Head, Single = true })), root =>
        {
            var isDeleted = root.GetProperty(DropboxProtocol.IsDeleted).GetBoolean();
            var entries = root.GetProperty(DropboxListing.EntriesName);
            if (entries.ValueKind != JsonValueKind.Array) throw new JsonException();
            var hasHead = entries.GetArrayLength() != 0;
            var head = hasHead ? entries[0] : default;
            if (hasHead && head.ValueKind != JsonValueKind.Object) throw new JsonException();
            // The route reports server_deleted beside entries; a value on the head element itself wins.
            var deleted = Field(head, DropboxProtocol.ServerDeleted);
            var time = Optional(deleted.ValueKind == JsonValueKind.Undefined
                ? Field(root, DropboxProtocol.ServerDeleted) : deleted);
            var hash = Optional(Field(head, DropboxEntries.ContentHashName));
            return (isDeleted, hasHead, time.Value, time.WrongType, RequiredType(head, DropboxEntries.IdName),
                RequiredType(head, DropboxEntries.RevName), RequiredType(head, DropboxEntries.PathDisplayName),
                hash.Value, hash.WrongType);
        });

    internal static partial DropboxReply<IReadOnlyList<(Entry? Entry, Exception? Error,
        bool KnownNoEffect, bool WriteThrottled)>> FinishBatch(PairConfig pair,
            IReadOnlyList<(string Path, bool ExpectedIsNull)> targets) =>
        Of(Object((DropboxListing.EntriesName, new Selection { Items = UploadMember, Limit = targets.Count })),
            root =>
        {
            var entries = root.GetProperty(DropboxListing.EntriesName);
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != targets.Count)
                throw new JsonException();
            var results = new (Entry? Entry, Exception? Error, bool KnownNoEffect, bool WriteThrottled)[targets.Count];
            for (int index = 0; index < results.Length; index++)
                results[index] = FinishedMember(entries[index], targets[index], pair);
            return (IReadOnlyList<(Entry? Entry, Exception? Error, bool KnownNoEffect, bool WriteThrottled)>)results;
        });

    // A malformed member keeps its ordinal: it is uncertain, and its valid siblings still settle.
    private static (Entry? Entry, Exception? Error, bool KnownNoEffect, bool WriteThrottled) FinishedMember(
        JsonElement member, (string Path, bool ExpectedIsNull) target, PairConfig pair)
    {
        try
        {
            var flat = member.GetProperty(DropboxWriter.MetadataName);
            var tag = DropboxEntries.RequiredString(flat, DropboxProtocol.Tag);
            if (tag == DropboxProtocol.TagSuccess)
                return (DropboxEntries.RequireLive(flat, pair, null, DropboxProtocol.TagSuccess), null, false, false);
            if (tag != DropboxProtocol.TagFailure) throw new JsonException();
            var (error, throttled) = DropboxErrors.MapUploadFailure(member.GetProperty(DropboxProtocol.TagFailure),
                target.ExpectedIsNull, target.Path);
            return (null, error, false, throttled);
        }
        catch (Exception error) when (IsItemFailure(error))
        {
            return (null, DropboxErrors.Refused(target.Path, error), false, false);
        }
    }

    internal static partial DropboxReply<FolderSubmitResult> FolderSubmit(PairConfig pair, int expectedCount) =>
        Of<FolderSubmitResult>(FolderStatus(expectedCount), root =>
        {
            var tag = DropboxEntries.RequiredString(root, DropboxProtocol.Tag);
            if (tag == DropboxProtocol.AsyncJobId)
                return new FolderSubmitResult.Accepted(NonEmpty(root, DropboxProtocol.AsyncJobId));
            if (tag == DropboxProtocol.TagComplete)
                return new FolderSubmitResult.Terminal(Members(root, pair, expectedCount));
            throw new JsonException();
        });

    internal static partial DropboxReply<FolderJobResult> FolderJob(PairConfig pair, int expectedCount) =>
        Of<FolderJobResult>(FolderStatus(expectedCount), root =>
        {
            var tag = DropboxEntries.RequiredString(root, DropboxProtocol.Tag);
            if (tag == DropboxProtocol.TagInProgress) return new FolderJobResult.InProgress();
            if (tag == DropboxProtocol.TagComplete)
                return new FolderJobResult.Terminal(Members(root, pair, expectedCount));
            // An explicit failure is terminal job evidence with unattributed members, not proof of no effect.
            if (tag == DropboxProtocol.TagFailed)
                return new FolderJobResult.Terminal(Members(default, pair, expectedCount));
            throw new JsonException();
        });

    private static Selection FolderStatus(int expectedCount) => Object((DropboxProtocol.Tag, Scalar),
        (DropboxProtocol.AsyncJobId, Scalar),
        (DropboxListing.EntriesName, new Selection { Items = FolderMember, Limit = expectedCount }));

    // Wrong cardinality proves job completion but attributes no member.
    private static IReadOnlyList<FolderMemberResult> Members(JsonElement root, PairConfig pair, int expectedCount)
    {
        var entries = Field(root, DropboxListing.EntriesName);
        var attributable = entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == expectedCount;
        var results = new FolderMemberResult[expectedCount];
        for (int ordinal = 0; ordinal < expectedCount; ordinal++)
            results[ordinal] = attributable ? Member(entries[ordinal], ordinal, pair)
                : new FolderMemberResult.Unknown(ordinal, null);
        return results;
    }

    private static FolderMemberResult Member(JsonElement member, int ordinal, PairConfig pair)
    {
        var tag = Field(member, DropboxProtocol.Tag);
        if (tag.ValueKind == JsonValueKind.String && tag.ValueEquals(DropboxProtocol.TagFailure))
            return DropboxErrors.MapFolderFailure(Field(member, DropboxProtocol.TagFailure), ordinal);
        if (tag.ValueKind != JsonValueKind.String || !tag.ValueEquals(DropboxProtocol.TagSuccess))
            return new FolderMemberResult.Unknown(ordinal, null);
        try
        {
            return new FolderMemberResult.Receipt(ordinal,
                DropboxEntries.RequireLive(Field(member, DropboxWriter.MetadataName), pair, null));
        }
        catch (Exception error) when (IsItemFailure(error))
        {
            return new FolderMemberResult.Unknown(ordinal, null);
        }
    }

    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;

    private static string NonEmpty(JsonElement value, string name)
    {
        var text = DropboxEntries.RequiredString(value, name);
        return text.Length == 0 ? throw new JsonException() : text;
    }

    private static (string? Value, bool WrongType) Optional(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => (value.GetString(), false),
        JsonValueKind.Undefined or JsonValueKind.Null => (null, false),
        _ => (null, true)
    };

    private static string? RequiredType(JsonElement value, string name)
    {
        var field = Optional(Field(value, name));
        return field.WrongType ? throw new JsonException() : field.Value;
    }

    private static bool IsItemFailure(Exception error) =>
        error is JsonException or InventoryIncompleteException || DropboxEntries.IsShapeFailure(error);

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
