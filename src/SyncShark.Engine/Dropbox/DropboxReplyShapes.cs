using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The closed reply shapes consumed by the Dropbox adapter, not an extensible decoder registry.</summary>
internal static partial class DropboxReplyShapes
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
}