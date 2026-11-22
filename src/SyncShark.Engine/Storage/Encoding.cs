using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Preserves the compact column formats and rejects malformed stored objects.</summary>
public static class Encoding
{
    /// <summary>v16 discovery_deleted_block.name_count capacity, shared by DDL, partition and codec.</summary>
    public const int DeletedBlockRows = 5000;
    // v16: at most three UTF-8 bytes per UTF-16 unit of a Windows extended path.
    private const int MaximumDeletedKeyBytes = 32768 * 3;
    private const int EnvelopeVersion = 1; // Existing v16 Version/Value wire discriminator.
    private static readonly UTF8Encoding HistoryUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Stored = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver
    {
        Modifiers = { info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            var ctor = info.Type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor is null) return;
            foreach (var property in info.Properties)
                if (property.AttributeProvider is System.Reflection.PropertyInfo field && ctor.GetParameters().Any(p =>
                    p.Name == field.Name && !p.HasDefaultValue)) property.IsRequired = true;
        } }
    } };

    /// <summary>Reject malformed pair JSON rather than installing default roots or policy.</summary>
    public static PairConfig DecodePair(string json)
    {
        var p = Decode<PairConfig>(json);
        if (string.IsNullOrEmpty(p.PairId) || p.Name is null || p.LocalRoot is null || p.DropboxRoot is null ||
            p.ConnectionId is null || !Enum.IsDefined(p.FirstRun) || p.IgnoreRules is null || p.IgnoreRules.Any(s => s is null) ||
            p.Exclusions is null || p.Exclusions.Any(e => e is null || e.Path is null || !Enum.IsDefined(e.Side)) ||
            p.OwedScopes?.Any(s => s is null || s.LocalPath is null || s.DropboxPath is null) == true ||
            p.Roots is {} root && (!root.IsValid() || root.LocalRoot != p.LocalRoot || root.DropboxRoot != p.DropboxRoot || root.ConnectionId != p.ConnectionId))
            throw new InvalidDataException();
        return p;
    }

    /// <summary>Read the numeric-enum subject stored on an issue row, including legacy unknown identities.</summary>
    public static IssueSubject DecodeSubject(string json) => ReadSubject(json, false);

    /// <summary>Read a v16 Version/Value issue-member subject; its enum values are names, never ordinals.</summary>
    public static IssueSubject DecodeMemberSubject(string envelope) => ReadSubject(envelope, true);

    private static IssueSubject ReadSubject(string json, bool envelope)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement;
            RejectDuplicates(value);
            if (envelope)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Version", out var version) ||
                    version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != EnvelopeVersion || !value.TryGetProperty("Value",out value))
                    throw new InvalidDataException();
            }
            var subject = value.Deserialize<IssueSubject>(envelope ? StoredEnvelope : Stored);
            if (subject?.Items is null || subject.Items.Any(i => i == null || i.Path == null || !Enum.IsDefined(i.Side)) ||
                subject.Incoming is {} side && !Enum.IsDefined(side) || !Enum.IsDefined(subject.FirstRunDeletion) ||
                subject.Move is {} move && (move.ExpectedId == null || move.Source == null || move.Destination == null)) throw new InvalidDataException();
            if (envelope)
            {
                // JsonStringEnumConverter also accepts case variants; the v16 persisted names are exact.
                var items = value.GetProperty(nameof(IssueSubject.Items)).EnumerateArray();
                foreach (var item in subject.Items)
                {
                    items.MoveNext();
                    if (items.Current.GetProperty(nameof(NoticeItem.Side)).GetString() != item.Side.ToString()) throw new InvalidDataException();
                }
                if (subject.Incoming is {} incoming && value.GetProperty(nameof(IssueSubject.Incoming)).GetString() != incoming.ToString() ||
                    value.TryGetProperty(nameof(IssueSubject.FirstRunDeletion),out var deletion) && deletion.GetString() != subject.FirstRunDeletion.ToString()) throw new InvalidDataException();
            }
            // v16 explicitly stored a null Id for an unknown identity. Never supply an absent Side or Path.
            return subject.Items.Any(i => i.Id == null) ? subject with { Items = subject.Items.Select(i => i.Id == null ? i with { Id = string.Empty } : i).ToArray() } : subject;
        }
        catch (JsonException e) { throw new InvalidDataException(null, e); }
    }

    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static readonly JsonSerializerOptions StoredEnvelope = new(EnvelopeOptions) { TypeInfoResolver = Stored.TypeInfoResolver };

    /// <summary>Write the v16 member envelope with stable enum names.</summary>
    public static string Envelope<T>(T value) => JsonSerializer.Serialize(new { Version = EnvelopeVersion, Value = value }, EnvelopeOptions);

    private static T Decode<T>(string json) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            RejectDuplicates(doc.RootElement);
            return doc.RootElement.Deserialize<T>(Stored) ?? throw new InvalidDataException();
        }
        catch (JsonException e) { throw new InvalidDataException(null, e); }
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    /// <summary>Packs canonical integer ids without changing opaque identity strings.</summary>
    public static object? LocalId(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
        id.ToString(CultureInfo.InvariantCulture) == value ? id : value;

    /// <summary>Packs a Local id by the same rule as <see cref="LocalId"/>; Dropbox ids stay opaque.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? IdFor(Side side, string? id) => side == Side.Local ? LocalId(id) : id;

    /// <summary>Packs canonical lowercase hashes and preserves opaque strings.</summary>
    public static object? Hash(string? value)
    {
        if (value is null || value.Length != 0 && value.Length != System.Security.Cryptography.SHA256.HashSizeInBytes * 2) return value;
        foreach (var character in value)
            if (character is not (>= FirstHexDigit and <= LastHexDigit or >= FirstHexLetter and <= LastHexLetter))
                return value;
        return Convert.FromHexString(value);
    }

    /// <summary>Decodes a packed hash or returns its original opaque text.</summary>
    public static string ReadHash(SqliteDataReader reader, int column)
    {
        var value = reader.GetValue(column);
        return value is byte[] bytes ? Convert.ToHexString(bytes).ToLowerInvariant()
            : value is string text ? text : reader.GetString(column);
    }

    /// <summary>Persisted booleans are nonzero, never only one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBool(SqliteDataReader reader, int column) => reader.GetInt64(column) != 0;

    /// <summary>Persisted UTC ticks as the zero-offset instant the engine stores.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset ReadTime(SqliteDataReader reader, int column) =>
        new(reader.GetInt64(column), TimeSpan.Zero);

    /// <summary>Decode the binding and its adjacent nullable journal/USN publication columns; the caller supplies the local key.</summary>
    public static SyncedEntry ReadSynced(SqliteDataReader reader, string localKey, int firstColumn)
    {
        var column = firstColumn;
        var localPath = reader.GetString(column++);
        var dropboxKey = reader.IsDBNull(column) ? localKey : reader.GetString(column);
        column++;
        var dropboxPath = reader.GetString(column++);
        var isFolder = ReadBool(reader, column++);
        var localId = reader.GetString(column++);
        var localSize = reader.GetInt64(column++);
        var localModified = reader.GetInt64(column++);
        var dropboxId = reader.GetString(column++);
        var hash = ReadHash(reader, column++);
        if (reader.IsDBNull(column) != reader.IsDBNull(column + 1)) throw new InvalidDataException();
        return new SyncedEntry(localKey, localPath, dropboxKey, dropboxPath, isFolder, localId, localSize,
            localModified, dropboxId, hash) { LocalPublished = reader.IsDBNull(column) ? null :
            new JournalPosition(unchecked((ulong)reader.GetInt64(column)), unchecked((ulong)reader.GetInt64(column + 1))) };
    }

    /// <summary>Decodes one scan entry; the local projection has no stored revision, hash or shared-root flag.</summary>
    public static Entry ReadEntry(SqliteDataReader reader, int firstColumn, bool localProjection)
    {
        var column = firstColumn;
        var path = reader.GetString(column++);
        var isFolder = ReadBool(reader, column++);
        var size = reader.GetInt64(column++);
        var modified = ReadTime(reader, column++);
        var id = reader.GetString(column++);
        if (localProjection)
            return new Entry(path, isFolder, size, modified, id, string.Empty, string.Empty,
                reader.IsDBNull(column) ? null : reader.GetString(column));
        var revision = reader.GetString(column++);
        var hash = ReadHash(reader, column++);
        var skip = reader.IsDBNull(column) ? null : reader.GetString(column);
        return new Entry(path, isFolder, size, modified, id, revision, hash, skip, ReadBool(reader, column + 1));
    }

    /// <summary>Pack one sorted block in the existing zlib format.</summary>
    public static byte[] EncodeDeletedNames(IReadOnlyList<string> keys)
    {
        if (keys.Count is <= 0 or > DeletedBlockRows) throw new InvalidDataException();
        using var output = new MemoryStream();
        using (var compressed = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(compressed, HistoryUtf8))
        {
            byte[]? previous = null;
            foreach (var key in keys)
            {
                if (HistoryUtf8.GetByteCount(key) is <= 0 or > MaximumDeletedKeyBytes) throw new InvalidDataException();
                var bytes = HistoryUtf8.GetBytes(key);
                ValidateDeletedKey(bytes, previous);
                writer.Write7BitEncodedInt(bytes.Length);
                writer.Write(bytes);
                previous = bytes;
            }
        }
        return output.ToArray();
    }

    // Yield one bounded key at a time, never allocate a corrupt declared length or a decoded block.
    /// <summary>Stream validated keys and check the block trailer on exhaustion.</summary>
    public static IEnumerable<string> DecodeDeletedNames(Stream input, int count, string lastKey, string? after)
    {
        if (count is <= 0 or > DeletedBlockRows) throw new InvalidDataException();
        // ZLibStream accepts a missing trailer; verify its Adler-32 explicitly before publishing any restored set.
        // RFC 1950: CMF + FLG, then a 32-bit Adler trailer.
        if (input.Length < 2 + sizeof(uint)) throw new InvalidDataException();
        var start = input.Position;
        input.Seek(-sizeof(uint), SeekOrigin.End);
        var trailer = new byte[sizeof(uint)];
        try { input.ReadExactly(trailer); }
        catch (EndOfStreamException error) { throw new InvalidDataException(null, error); }
        input.Position = start;
        var expectedChecksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(trailer);
        uint checksum = 1;
        using var compressed = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(compressed, HistoryUtf8, leaveOpen: true);
        byte[]? previous = after is null ? null : HistoryUtf8.GetBytes(after);
        string? last = null;
        for (var index = 0; index < count; index++)
        {
            int length;
            try { length = reader.Read7BitEncodedInt(); }
            catch (Exception error) when (error is EndOfStreamException or FormatException)
            { throw new InvalidDataException(null, error); }
            if (length is <= 0 or > MaximumDeletedKeyBytes) throw new InvalidDataException();
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException();
            ValidateDeletedKey(bytes, previous);
            checksum = DeletedChecksum(checksum, bytes);
            try { last = HistoryUtf8.GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException(null, error); }
            previous = bytes;
            yield return last;
        }
        if (last != lastKey || compressed.ReadByte() != -1 || checksum != expectedChecksum) throw new InvalidDataException();
    }

    private static uint DeletedChecksum(uint checksum, byte[] bytes)
    {
        // RFC 1950 Adler-32: two 16-bit sums modulo the largest prime below 2^16.
        const uint adlerModulus = 65521;
        const int halfBits = sizeof(ushort) * 8;
        const int lengthBits = 7; // BinaryWriter.Write7BitEncodedInt format.
        const int more = 1 << lengthBits;
        var a = checksum & ushort.MaxValue;
        var b = checksum >> halfBits;
        // The persisted length prefix is the canonical BinaryWriter seven-bit encoding.
        var remaining = bytes.Length;
        do
        {
            var value = (byte)(remaining & (more - 1));
            remaining >>= lengthBits;
            Add(remaining == 0 ? value : (byte)(value | more));
        } while (remaining != 0);
        foreach (var value in bytes) Add(value);
        return (b << halfBits) | a;

        void Add(byte value)
        {
            a = (a + value) % adlerModulus;
            b = (b + a) % adlerModulus;
        }
    }

    private static void ValidateDeletedKey(byte[] key, byte[]? previous)
    {
        if (key.Length is <= 0 or > MaximumDeletedKeyBytes ||
            previous is not null && previous.AsSpan().SequenceCompareTo(key) >= 0)
            throw new InvalidDataException();
    }

    // Lowercase hexadecimal is the existing packed-hash representation, not a tunable alphabet.
    private const char FirstHexDigit = '0';
    private const char LastHexDigit = '9';
    private const char FirstHexLetter = 'a';
    private const char LastHexLetter = 'f';

}
