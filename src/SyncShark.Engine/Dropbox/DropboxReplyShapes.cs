using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The closed reply shapes consumed by the Dropbox adapter, not an extensible decoder registry.</summary>
public static class DropboxReplyShapes
{
    private static readonly DropboxErrors Errors = new();
    private static readonly Selection Scalar = new();
    private static readonly Selection MetadataValue = new()
    {
        Metadata = true,
        Names = DropboxEntries.Projection.Fields,
        Fields = new Selection?[DropboxEntries.Projection.Fields.Length]
    };
    private static readonly Selection Failure = FailureUnion("conflict", "malformed_path", "lookup_failed", "correct_offset");
    private static readonly Selection UploadMember = UploadMemberValue();
    private static readonly Selection FolderMember = Object((".tag", Scalar),
        ("metadata", MetadataValue), ("failure", Failure));
    private static readonly Selection MoveMember = Object((".tag", Scalar),
        ("success", MetadataValue), ("failure",
            FailureUnion("relocation_error", "from_lookup", "from_write",
                "to", "conflict", "malformed_path")));
    private static readonly Selection Head = Object((DropboxEntries.IdName, Scalar), (DropboxEntries.RevName, Scalar),
        (DropboxEntries.PathDisplayName, Scalar), ("server_deleted", Scalar),
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
    private static Selection FailureUnion(params string[] extra)
    {
        string[] names = [".tag", "path", "reason",
            "path_lookup", "path_write", .. extra];
        var fields = new Selection?[names.Length];
        var union = new Selection { Names = names, Fields = fields };
        System.Array.Fill(fields, union);
        return union;
    }

    // A finish member flattens its metadata beside .tag; the failure is the one extra field.
    private static Selection UploadMemberValue()
    {
        var count = DropboxEntries.Projection.Fields.Length;
        string[] names = [.. DropboxEntries.Projection.Fields, "failure"];
        var fields = new Selection?[names.Length];
        fields[count] = Failure;
        return new Selection { Metadata = true, Names = names, Fields = fields };
    }

    private static DropboxReply<T> Of<T>(Selection? root, Func<JsonElement, T> complete) =>
        new(root, (value, _) => ValueTask.FromResult(complete(value)));

    public static DropboxReply<DropboxPage> Page(PairConfig pair)
    {
        var page = new PageSpool(pair);
        var entries = new Selection { Items = MetadataValue, Starts = page.Reset, Sink = page.Add };
        return new(Object(("cursor", Scalar), ("has_more", Scalar),
            ("entries", entries)), page.SealAsync, page.Begin, page.AbortAsync, page.DrainAsync);
    }

    public static DropboxReply<Listed> Listed(PairConfig pair) =>
        Of(MetadataValue, root => DropboxEntries.Parse(root, pair));

    public static DropboxReply<Entry> Metadata(PairConfig pair, string? untaggedKind = null, bool nested = false) => nested
        ? Of(Object(("metadata", MetadataValue)), root =>
            DropboxEntries.RequireLive(root.GetProperty("metadata"), pair, untaggedKind))
        : Of(MetadataValue, root => DropboxEntries.RequireLive(root, pair, untaggedKind));

    public static DropboxReply<string> SessionId() => RequiredText("session_id");

    public static DropboxReply<ValueTuple> Void() => Of<ValueTuple>(null, _ => default);

    public static DropboxReply<string> Cursor() => RequiredText("cursor");

    private static DropboxReply<string> RequiredText(string name) =>
        Of(Object((name, Scalar)), root => DropboxEntries.RequiredString(root, name));

    public static DropboxReply<(bool Changes, TimeSpan? Backoff)> Longpoll() =>
        Of<(bool Changes, TimeSpan? Backoff)>(Object(("changes", Scalar),
            ("backoff", Scalar)), root =>
        {
            var changes = root.GetProperty("changes").GetBoolean();
            var backoff = Field(root, "backoff");
            if (backoff.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return (changes, (TimeSpan?)null);
            var seconds = backoff.GetInt64();
            if (seconds < 0) throw new JsonException();
            return (changes, TimeSpan.FromSeconds(seconds));
        });

    public static DropboxReply<(string AccountId, string RootNamespaceId,
        string HomeNamespaceId)> AccountRoot() =>
        Of(Object(("account_id", Scalar), ("root_info",
            Object(("root_namespace_id", Scalar), ("home_namespace_id", Scalar)))), root =>
        {
            var info = root.GetProperty("root_info");
            return (NonEmpty(root, "account_id"), NonEmpty(info, "root_namespace_id"),
                NonEmpty(info, "home_namespace_id"));
        });

    public static DropboxReply<(bool IsDeleted, bool HasHead, string? ServerDeleted,
        bool TimeWrongType, string? Id, string? Revision, string? NativePath,
        string? ContentHash, bool HashWrongType)> HistoryHead() =>
        Of(Object(("is_deleted", Scalar), ("server_deleted", Scalar),
            ("entries", new Selection { Items = Head, Single = true })), root =>
        {
            var isDeleted = root.GetProperty("is_deleted").GetBoolean();
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array) throw new JsonException();
            var hasHead = entries.GetArrayLength() != 0;
            var head = hasHead ? entries[0] : default;
            if (hasHead && head.ValueKind != JsonValueKind.Object) throw new JsonException();
            // The route reports server_deleted beside entries; a value on the head element itself wins.
            var deleted = Field(head, "server_deleted");
            var time = Optional(deleted.ValueKind == JsonValueKind.Undefined
                ? Field(root, "server_deleted") : deleted);
            var hash = Optional(Field(head, DropboxEntries.ContentHashName));
            return (isDeleted, hasHead, time.Value, time.WrongType, RequiredType(head, DropboxEntries.IdName),
                RequiredType(head, DropboxEntries.RevName), RequiredType(head, DropboxEntries.PathDisplayName),
                hash.Value, hash.WrongType);
        });

    private const string Create = "files/create_folder_batch";
    private const string Delete = "files/delete_batch";
    private const string Move = "files/move_batch_v2";
    private const string Upload = "files/upload_session/finish_batch_v2";

    private static Selection Batch(string route, int count) => Object((".tag", Scalar), ("async_job_id", Scalar),
        ("entries", new Selection { Items = route switch { Move => MoveMember, Upload => UploadMember,
            Create or Delete => FolderMember, _ => throw new ArgumentOutOfRangeException(nameof(route)) }, Limit = count }));

    /// <summary>Decode a batch launch token or a synchronous ordered result for the actual send.</summary>
    public static DropboxReply<(AcceptedJob? Accepted, JobReply Reply)> Submit(PairConfig pair, string route,
        IReadOnlyList<JobMember> launch) => Of(Batch(route, launch.Count), root => Submit(root, pair, route, launch));

    /// <summary>Interpret an already validated submit body using the same rules as the streaming decoder.</summary>
    public static (AcceptedJob? Accepted, JobReply Reply) Submit(JsonElement root, PairConfig pair, string route,
        IReadOnlyList<JobMember> launch)
    {
        if (route is not (Create or Move or Delete or Upload)) throw new ArgumentOutOfRangeException(nameof(route));
        var tag = Tag(root);
        if (route != Upload && tag == "async_job_id")
            return (new AcceptedJob(pair.PairId, route, NonEmpty(root, "async_job_id"), launch.ToArray()), new JobReply.Running());
        if (route == Upload ? Field(root, ".tag").ValueKind != JsonValueKind.Undefined : tag != "complete") throw new JsonException();
        return (null, Terminal(root, pair, route, launch));
    }

    /// <summary>Decode a check of an already accepted job against its saved selection and configured root.</summary>
    public static DropboxReply<JobReply> Job(PairConfig pair, AcceptedJob job) =>
        Of(Batch(job.Route, job.Launch.Count), root => Job(root, job, pair));

    /// <summary>Rootless status can report job state but cannot attribute a metadata receipt.</summary>
    public static JobReply Job(JsonElement response, AcceptedJob job) => Job(response, job, null);

    /// <summary>Match terminal evidence to the original member ordinal, never to an inferred request spelling.</summary>
    public static JobReply Job(JsonElement response, AcceptedJob job, PairConfig? pair) => Tag(response) switch
    {
        "in_progress" => new JobReply.Running(),
        "invalid_async_job_id" => new JobReply.Invalid(),
        "complete" => Terminal(response, pair?.PairId == job.PairId ? pair : null, job.Route, job.Launch),
        "failed" => Terminal(default, pair, job.Route, job.Launch),
        _ => throw new JsonException()
    };

    private static JobReply.Terminal Terminal(JsonElement root, PairConfig? pair, string route, IReadOnlyList<JobMember> launch)
    {
        var entries = Field(root, "entries");
        var members = new (Entry?, TextKey?, FailureDisposition?)[launch.Count];
        HashSet<int>? absentOrdinals = null;
        bool matched = entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == members.Length;
        if (!matched) Array.Fill(members, Unknown);
        else
        {
            int i = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                members[i] = Member(entry, pair, route, launch[i], out bool absent);
                if (absent) (absentOrdinals ??= []).Add(i);
                ++i;
            }
        }
        var terminal = new JobReply.Terminal(members);
        return absentOrdinals is null ? terminal : terminal with { AbsentOrdinals = absentOrdinals };
    }

    private static (Entry?, TextKey?, FailureDisposition?) Unknown => (null, null, FailureDisposition.UnknownEffect);

    private static (Entry?, TextKey?, FailureDisposition?) Member(JsonElement member, PairConfig? pair, string route, JobMember selected, out bool absent)
    {
        absent = false;
        try
        {
            var metadata = route == Upload ? member : Field(member, route == Move ? "success" : "metadata");
            var tag = Tag(member);
            if (pair is null) return Unknown;
            if (tag == "failure")
            {
                var result = Errors.ClassifyMember(Field(member, "failure"), route, expectedIsNull: selected.Id is null);
                absent = result.Absent && route == Delete && selected.Id is { Length: > 3 } selector && selector.StartsWith("id:", StringComparison.Ordinal);
                return (null, result.Cause, result.Disposition);
            }
            if (tag != "success") return Unknown;
            var entry = route == Upload ? DropboxEntries.RequireLive(metadata, pair, null, "success") :
                DropboxReply.Metadata(metadata, pair, route == Create ? "folder" : null);
            // A by-ID delete targets the identity, not the original feed footprint.
            var byIdDelete = route == Delete && !string.IsNullOrWhiteSpace(selected.Id);
            if (string.IsNullOrEmpty(entry.Id) || route == Create && !entry.IsFolder ||
                selected.Id is { } id && entry.Id != id ||
                !byIdDelete && !PathNames.Same(entry.Path, selected.Destination ?? selected.Path)) return Unknown;
            return (entry, null, null);
        }
        catch (Exception error) when (IsItemFailure(error)) { return Unknown; }
    }

    private static string? Tag(JsonElement value) => Field(value, ".tag") is { ValueKind: JsonValueKind.String } tag ? tag.GetString() : null;

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
    // Entries encode once into a pending buffer; only whole chunks, then the final tail, are written to the spool,
    // each by one awaited write the spool admits against free space. An unwritten tail is never written on abort.
    private sealed class PageSpool(PairConfig pair)
    {
        private static readonly int Chunk = DropboxReply.ChunkBytes;
        private Stream? _spool;
        private MemoryStream? _pending;
        private BinaryWriter? _writer;
        private Exception? _failure;

        internal void Begin()
        {
            _spool = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, bufferSize: DropboxReply.ChunkBytes,
                options: FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            _pending = new MemoryStream();
            _writer = new BinaryWriter(_pending, Encoding.UTF8, leaveOpen: true);
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
            var cursor = DropboxEntries.RequiredString(root, "cursor");
            var hasMore = root.GetProperty("has_more").GetBoolean();
            if (root.GetProperty("entries").ValueKind != JsonValueKind.Array) throw new JsonException();
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
