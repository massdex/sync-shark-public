using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The one Dropbox metadata parser; reads client_modified only.</summary>
public sealed partial class DropboxEntries
{
    /// <summary>Read one live entry or path-only deletion.</summary>
    public Listed Decode(JsonElement entry, PairConfig pair) => Parse(entry, pair);

    // The metadata field names, readable only through this owner.
    internal static string IdName => "id";
    internal static string RevName => "rev";
    internal static string PathDisplayName => "path_display";
    internal static string ContentHashName => "content_hash";

    // JSON surrogate tokens preserve grammar/depth without retaining discarded input tokens.
    private const string EmptyObjectToken = "{}";
    private const string EmptyArrayToken = "[]";
    // An item is the reply object at depth 1, so its sharing_info member object is at depth 2.
    private const int SharingDepth = 2;

    internal static partial Entry RequireLive(JsonElement metadata, PairConfig pair,
        string? untaggedKind, string acceptedFileTag);

    public static partial void ValidateUploadResult(Entry returned, string requestedPath,
        long uploadedBytes, string uploadedHash);

    /// <summary>Retains only winning metadata fields until one complete item can reach Parse or RequireLive.</summary>
    internal sealed partial class Projection : IDisposable
    {
        /// <summary>Transfers the completed selected-field document; duplicate fields replace prior values
        /// and semantic errors. The caller disposes it after parsing and staging the item.</summary>
        internal partial JsonDocument Complete();

        internal partial void Accept(JsonTokenType token, int field, byte[]? scalar);

        public partial void Dispose();

        internal static readonly string[] Fields =
        [
            "path_display", ".tag", "id",
            "size", "client_modified", "rev",
            "content_hash", "is_downloadable", "sharing_info"
        ];
        // sharing_info's one captured member: a mount point carries shared_folder_id there.
        private static readonly string[] SharingFields = ["shared_folder_id"];
        private static readonly int SharingField = Array.IndexOf(Fields, "sharing_info");

        private readonly byte[]?[] _values = new byte[Fields.Length][];
        private byte[]? _sharedFolderId;
        private bool _sharing;
        private bool _sharingField;
        private int _depth;
        private int _field = ReplyTokens.NoField;
        private bool _started;
        private bool _rootObject;
        private bool _complete;
        private bool _disposed;

        /// <summary>The names the tokenizer matches next: the item's fields, or inside sharing_info its one member.</summary>
        internal ReadOnlySpan<string> Names =>
            _depth == 1 && _rootObject ? Fields : _sharing && _depth == SharingDepth ? SharingFields : ReadOnlySpan<string>.Empty;

        /// <summary>Whether the tokenizer keeps the next scalar: a selected field's value.</summary>
        internal bool Retain => (_depth == 1 && _rootObject && _field >= 0) || _sharingField;

        // One job: a token's single transition, which updates the selected fields and the nesting depth together.
        internal partial void Accept(JsonTokenType token, int field, byte[]? scalar)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_complete) throw new JsonException();
            if (token == JsonTokenType.PropertyName)
            {
                if (_depth == 1 && _rootObject)
                {
                    if (field >= Fields.Length || field < ReplyTokens.NoField) throw new JsonException();
                    _field = field;
                }
                else if (_sharing && _depth == SharingDepth) _sharingField = field == 0;
                return;
            }
            var container = token is JsonTokenType.StartObject or JsonTokenType.StartArray;
            if (!_started)
            {
                _started = true;
                _rootObject = token == JsonTokenType.StartObject;
            }
            else if (_depth == 1 && _rootObject && _field >= 0)
            {
                // A wrong-shaped field replaces the prior value but never captures its descendants.
                _values[_field] = container
                    ? System.Text.Encoding.UTF8.GetBytes(token == JsonTokenType.StartObject ? EmptyObjectToken : EmptyArrayToken)
                    : scalar;
                // Only an object sharing_info is entered; a repeated field also replaces its captured member.
                if (_field == SharingField)
                {
                    _sharing = token == JsonTokenType.StartObject;
                    _sharedFolderId = null;
                }
                _field = ReplyTokens.NoField;
            }
            else if (_sharingField)
            {
                _sharedFolderId = container
                    ? System.Text.Encoding.UTF8.GetBytes(token == JsonTokenType.StartObject ? EmptyObjectToken : EmptyArrayToken)
                    : scalar;
                _sharingField = false;
            }
            if (container) _depth++;
            else if (token is JsonTokenType.EndObject or JsonTokenType.EndArray) _depth--;
            if (_depth < SharingDepth) _sharing = false;
            if (_depth == 0) _complete = true;
        }

        internal partial JsonDocument Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_complete) throw new JsonException();
            // Keep a nonobject root nonobject so the existing metadata boundary rejects its shape.
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                if (!_rootObject) writer.WriteNullValue();
                else
                {
                    writer.WriteStartObject();
                    for (var i = 0; i < Fields.Length; i++)
                    {
                        if (_values[i] is not { } value) continue;
                        writer.WritePropertyName(Fields[i]);
                        if (i == SharingField && _sharedFolderId is { } shared)
                        {
                            writer.WriteStartObject();
                            writer.WritePropertyName(SharingFields[0]);
                            writer.WriteRawValue(shared, skipInputValidation: true);
                            writer.WriteEndObject();
                        }
                        else writer.WriteRawValue(value, skipInputValidation: true);
                    }
                    writer.WriteEndObject();
                }
            }
            return JsonDocument.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length)));
        }

        public partial void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Array.Clear(_values);
            _sharedFolderId = null;
        }
    }

    /// <summary>A deleted item → Listed(path, null); otherwise an entry, with Skip for reserved names or non-downloadable files.</summary>
    public static Listed Parse(JsonElement metadata, PairConfig pair, string? untaggedKind = null) =>
        Parse(metadata, pair, untaggedKind, "file");

    private static Listed Parse(JsonElement metadata, PairConfig pair, string? untaggedKind, string acceptedFileTag)
    {
        var path = DropboxPaths.Relative(pair, RequiredString(metadata, PathDisplayName));
        // Only the flattened upload-success decoder substitutes its accepted file tag.
        var tag = metadata.TryGetProperty(".tag", out var kind)
            ? kind.ValueEquals(acceptedFileTag) ? acceptedFileTag : kind.GetString() ?? throw new JsonException()
            : untaggedKind ?? throw new KeyNotFoundException();
        if (tag == "deleted") return new Listed(path, null);

        var skip = path.Length != 0 && !PathNames.IsTransferable(path)
            ? nameof(TextKey.SkipReservedName) : null;
        if (tag == "folder")
        {
            return new Listed(path, new Entry(path, true, 0L, default,
                RequiredString(metadata, "id"), string.Empty, string.Empty, skip, SharedRoot(metadata)));
        }

        if (tag != acceptedFileTag) throw new JsonException();

        if (metadata.TryGetProperty("is_downloadable", out var downloadable) && !downloadable.GetBoolean())
            skip ??= nameof(TextKey.SkipNotDownloadable);
        return new Listed(path, new Entry(path, false,
            metadata.GetProperty("size").GetInt64(),
            metadata.GetProperty("client_modified").GetDateTimeOffset(),
            RequiredString(metadata, "id"),
            RequiredString(metadata, "rev"),
            metadata.TryGetProperty("content_hash", out var hash)
                ? hash.GetString() ?? throw new JsonException() : string.Empty, skip));
    }

    // A mount point names its shared folder in sharing_info; parent containment alone does not. A wrong shape
    // is a decode failure, never "not a mount".
    private static bool SharedRoot(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("sharing_info", out var sharing)) return false;
        if (sharing.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (!sharing.TryGetProperty("shared_folder_id", out var id)) return false;
        if (id.ValueKind != JsonValueKind.String) throw new JsonException();
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string RequiredString(JsonElement value, string name) =>
        value.GetProperty(name).GetString() ?? throw new JsonException();

    /// <summary>The extraction exceptions a reply decoder may wrap; not JSON, I/O, transport or cancellation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsShapeFailure(Exception error) =>
        error is KeyNotFoundException or InvalidOperationException or FormatException;

    /// <summary>The live entry a write reply must carry; a tombstone is a decode failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Entry RequireLive(JsonElement metadata, PairConfig pair, string? untaggedKind) =>
        RequireLive(metadata, pair, untaggedKind, "file");

    internal static partial Entry RequireLive(JsonElement metadata, PairConfig pair,
        string? untaggedKind, string acceptedFileTag) =>
        Parse(metadata, pair, untaggedKind, acceptedFileTag).Entry ?? throw new JsonException();

    public static partial void ValidateUploadResult(Entry returned, string requestedPath,
        long uploadedBytes, string uploadedHash)
    {
        if (returned.IsFolder || string.IsNullOrEmpty(returned.Path) ||
            !PathNames.Same(returned.Path, requestedPath) || string.IsNullOrEmpty(returned.Id) ||
            string.IsNullOrEmpty(returned.Revision) || returned.Size != uploadedBytes ||
            (!ContentHash.IsValid(uploadedHash) || !string.Equals(uploadedHash, returned.ContentHash, StringComparison.OrdinalIgnoreCase)))
            throw new ItemFailedException(requestedPath, TextKey.DropboxRefused, null);
    }
}
