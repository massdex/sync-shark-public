using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Lossless compact values at the Storage boundary; opaque contract values stay opaque.</summary>
internal static class StateEncoding
{
    // Windows supports 32,768 UTF-16 units; UTF-8 needs at most three bytes per unit.
    internal const int MaximumDeletedKeyBytes = SyncEndpointContext.MaximumPathLength * StateSchema.DeletedKeyUtf8BytesPerUnit;
    private static readonly UTF8Encoding HistoryUtf8 = new(false, true);

    internal static byte[] EncodeDeletedNames(IReadOnlyList<string> keys)
    {
        if (keys.Count is <= 0 or > StateSchema.DeletedHistoryBatchSize) throw new InvalidDataException();
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
    internal static IEnumerable<string> DecodeDeletedNames(Stream input, int count, string lastKey, string? after)
    {
        if (count is <= 0 or > StateSchema.DeletedHistoryBatchSize) throw new InvalidDataException();
        // ZLibStream accepts a missing trailer; verify its Adler-32 explicitly before publishing any restored set.
        if (input.Length < StateSchema.DeletedZlibMinimumBytes) throw new InvalidDataException();
        var start = input.Position;
        input.Seek(-StateSchema.DeletedChecksumBytes, SeekOrigin.End);
        var trailer = new byte[StateSchema.DeletedChecksumBytes];
        try { input.ReadExactly(trailer); }
        catch (EndOfStreamException error) { throw new InvalidDataException(null, error); }
        input.Position = start;
        var expectedChecksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(trailer);
        uint checksum = StateSchema.DeletedChecksumInitial;
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
        if (last != lastKey || compressed.ReadByte() != StateSchema.DeletedStreamEnd || checksum != expectedChecksum) throw new InvalidDataException();
    }

    private static uint DeletedChecksum(uint checksum, byte[] bytes)
    {
        var a = checksum & StateSchema.DeletedChecksumMask;
        var b = checksum >> StateSchema.DeletedChecksumShift;
        // The persisted length prefix is the canonical BinaryWriter seven-bit encoding.
        var remaining = bytes.Length;
        do
        {
            var value = (byte)(remaining & StateSchema.DeletedLengthMask);
            remaining >>= StateSchema.DeletedLengthShift;
            Add(remaining == 0 ? value : (byte)(value | StateSchema.DeletedLengthContinuation));
        } while (remaining != 0);
        foreach (var value in bytes) Add(value);
        return (b << StateSchema.DeletedChecksumShift) | a;

        void Add(byte value)
        {
            a = (a + value) % StateSchema.DeletedChecksumModulus;
            b = (b + a) % StateSchema.DeletedChecksumModulus;
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

    // Enums stay explicit names, and the relaxed escaping is the compact form canonical keys require.
    private static readonly JsonSerializerOptions Persisted = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new MemberResultConverter() }
    };

    private static readonly ConcurrentDictionary<Type, Member[]> Shapes = new();

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
        if (value is null || value.Length != 0 && value.Length != StateSchema.HashHexLength) return value;
        foreach (var character in value)
            if (character is not (>= FirstHexDigit and <= LastHexDigit or >= FirstHexLetter and <= LastHexLetter))
                return value;
        return Convert.FromHexString(value);
    }

    /// <summary>Decodes a packed hash or returns its original opaque text.</summary>
    public static string ReadHash(SqliteDataReader reader, int column)
    {
        var value = reader.GetValue(column);
        return value is byte[] bytes ? Hex(bytes)
            : value is string text ? text : reader.GetString(column);
    }

    /// <summary>Persisted booleans are nonzero, never only one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBool(SqliteDataReader reader, int column) => reader.GetInt64(column) != 0;

    /// <summary>Persisted UTC ticks as the zero-offset instant the engine stores.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset ReadTime(SqliteDataReader reader, int column) =>
        new(reader.GetInt64(column), TimeSpan.Zero);

    /// <summary>Deserializes one engine-persisted value; a null result is invalid stored data.</summary>
    public static T Decode<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException();

    /// <summary>Strictly decodes a pair; missing or null roots are unbound, malformed roots fail closed.</summary>
    public static PairConfig DecodePair(string json) => DecodeStrict(json, static (root, _) =>
    {
        RequireBinding(root);
        // Pair JSON keeps its existing format, so its numeric enums are read with the default options.
        var pair = root.Deserialize<PairConfig>() ?? throw new InvalidDataException();
        if (pair.PairId is null || pair.Name is null || pair.LocalRoot is null || pair.DropboxRoot is null ||
            pair.ConnectionId is null || pair.IgnoreRules is null || pair.Exclusions is null ||
            pair.IgnoredFolderKeepRules?.Contains(null!) == true)
            throw new InvalidDataException();
        return pair;
    });

    /// <summary>Classifies stored pending JSON as none, validated current or byte-preserved legacy.</summary>
    public static PendingRead DecodePending(string? json) =>
        json is null ? new NoPending() : DecodeStrict<PendingRead>(json, ReadPendingNote);

    /// <summary>The exact persisted text of a current note; pending compare-and-swap relies on these bytes.</summary>
    public static string EncodePending(PendingEffect value) =>
        JsonSerializer.Serialize(RequireCurrent(value), Persisted);

    /// <summary>Wraps a value in the version-1 envelope; a null value is an encoded fact, not SQL NULL.</summary>
    public static string Envelope<T>(T value) =>
        JsonSerializer.Serialize(new VersionedValue<T>((int)EncodingFormat.Current, value), Persisted);

    /// <summary>The envelope for a nullable column, where a null value is SQL NULL.</summary>
    public static string? OptionalEnvelope<T>(T? value) where T : class => value is null ? null : Envelope(value);

    /// <summary>Decodes the nine saved fields from <paramref name="firstColumn"/>; the caller supplies the local key.</summary>
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
        return new SyncedEntry(localKey, localPath, dropboxKey, dropboxPath, isFolder, localId, localSize,
            localModified, dropboxId, ReadHash(reader, column));
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

    /// <summary>Strictly decodes a required version-1 envelope column.</summary>
    public static T ReadEnvelope<T>(SqliteDataReader reader, int column) where T : class =>
        DecodeEnvelope<T>(reader.GetString(column)) ?? throw new InvalidDataException();

    /// <summary>The items of a required issue-subject envelope column that carry a file id (B-195). An item stored with
    /// no file id (id unknown) is skipped, so it never proves a replacement; everything else is decoded as strictly as
    /// <see cref="ReadEnvelope{T}"/>.</summary>
    public static IssueItem[] ReadIdentifiedItems(SqliteDataReader reader, int column) =>
        ReadSubjectItems(reader, column).Where(static item => item.Id is not null).ToArray();

    /// <summary>Every item of a required issue-subject envelope column, an item with no file id included (B-198); only
    /// that null id is admitted, everything else is decoded as strictly as <see cref="ReadEnvelope{T}"/>.</summary>
    public static IReadOnlyList<IssueItem> ReadSubjectItems(SqliteDataReader reader, int column) =>
        (DecodeStrict(reader.GetString(column), static (root, _) => DecodeValue<IssueSubject>(root, unknownIds: true))
            ?? throw new InvalidDataException()).Items;

    /// <summary>Strictly decodes a nullable version-1 envelope column.</summary>
    public static T? ReadOptionalEnvelope<T>(SqliteDataReader reader, int column) where T : class =>
        reader.IsDBNull(column) ? null : ReadEnvelope<T>(reader, column);

    /// <summary>Decodes a persisted enum by explicit name; numbers and unknown names are invalid.</summary>
    public static T ReadEnum<T>(SqliteDataReader reader, int column) where T : struct, Enum =>
        reader.GetValue(column) is string text && Enum.IsDefined(typeof(T), text)
            ? Enum.Parse<T>(text) : throw new InvalidDataException();

    /// <summary>Decodes a nullable persisted enum by the rule of <see cref="ReadEnum{T}"/>.</summary>
    public static T? ReadOptionalEnum<T>(SqliteDataReader reader, int column) where T : struct, Enum =>
        reader.IsDBNull(column) ? null : ReadEnum<T>(reader, column);

    /// <summary>Decodes adjacent attempts and UTC ticks columns; both null means no retry episode.</summary>
    public static V2.FailureRetryOwner.State? ReadRetryState(SqliteDataReader reader, int firstColumn)
    {
        var ticksColumn = firstColumn + 1;
        var none = reader.IsDBNull(firstColumn);
        if (none != reader.IsDBNull(ticksColumn)) throw new InvalidDataException();
        if (none) return null;
        var attempts = reader.GetInt64(firstColumn);
        if (attempts < 0 || attempts > int.MaxValue) throw new InvalidDataException();
        return new V2.FailureRetryOwner.State((int)attempts, ReadTime(reader, ticksColumn));
    }

    /// <summary>Decodes adjacent lifecycle columns; all null means no lifecycle, a partial set is invalid.</summary>
    public static (string Episode, long Revision, long WorkId)? ReadItemLifecycle(
        SqliteDataReader reader, int firstColumn)
    {
        var column = firstColumn;
        var episode = ReadOptionalText(reader, column++);
        long? revision = reader.IsDBNull(column) ? null : reader.GetInt64(column);
        column++;
        long? workId = reader.IsDBNull(column) ? null : reader.GetInt64(column);
        if (episode is null && revision is null && workId is null) return null;
        if (string.IsNullOrEmpty(episode) || revision is null || revision < 0 || workId is null || workId < 1)
            throw new InvalidDataException();
        return (episode, revision.Value, workId.Value);
    }

    /// <summary>The canonical registration key; a changed identity or preimage is another obligation.</summary>
    public static string ObligationKey(Side side, string domain, ObligationRole role, string? identity,
        string pathKey, string? oldPathKey, SyncedEntry? before)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        if (identity?.Length == 0) throw new ArgumentException(null, nameof(identity));
        return Canonical([(int)EncodingFormat.Current, (int)side, domain, role, IdFor(side, identity), pathKey,
            oldPathKey, before]);
    }

    /// <summary>The canonical source-less work key of one issue lifecycle.</summary>
    public static string IssueWorkKey(long issueId, IssueSubject subject)
    {
        // Each item's canonical text orders it, so a number and text with equal digits stay distinct.
        var items = subject.Items
            .Select(static item => new object?[]
            {
                (int)item.Side, PathNames.KeyFor(item.Side, item.Path),
                string.IsNullOrEmpty(item.Id) ? null : IdFor(item.Side, item.Id)
            })
            .DistinctBy(Canonical).OrderBy(Canonical, StringComparer.Ordinal).ToArray();
        return Canonical([(int)EncodingFormat.Current, issueId, items,
            subject.Incoming is { } incoming ? (int)incoming : null]);
    }

    /// <summary>Decodes one operation row with its separately read disposition; the phase rules both.</summary>
    public static FolderRemovalState ReadFolderRemoval(SqliteDataReader reader,
        FolderRetirementDisposition? disposition)
    {
        var phase = ReadEnum<FolderRemovalPhase>(reader, StateSchema.FolderPhase);
        var active = ReadBool(reader, StateSchema.FolderActive);
        var settled = phase is FolderRemovalPhase.Retained or FolderRemovalPhase.Unknown;
        if (active ? phase == FolderRemovalPhase.Complete || disposition is not null
                : !settled && (phase != FolderRemovalPhase.Complete || disposition is not null))
            throw new InvalidDataException();
        return new FolderRemovalState(
            ReadId(reader, StateSchema.FolderOperationId),
            ReadCount(reader, StateSchema.FolderGeneration),
            phase,
            ReadSide(reader, StateSchema.FolderDeletingSide),
            ReadEnvelope<Entry>(reader, StateSchema.FolderRootEntryJson),
            DecodeEnvelope<Entry>(reader.GetString(StateSchema.FolderSourceEvidenceJson)),
            ReadOptionalEnvelope<SyncedEntry>(reader, StateSchema.FolderSavedRootJson),
            ReadEnvelope<RowContext>(reader, StateSchema.FolderRootContextJson),
            ReadOptionalText(reader, StateSchema.FolderCursor),
            active,
            ReadOptionalEnum<TextKey>(reader, StateSchema.FolderCause),
            disposition,
            ReadEnum<FolderRemovalAuthority>(reader, StateSchema.FolderAuthority))
        { PreRestoreId = ReadOptionalText(reader, StateSchema.FolderPreRestoreId) };
    }

    /// <summary>Decodes one member row and refuses facts its phase cannot have.</summary>
    public static FolderRemovalItem ReadFolderItem(SqliteDataReader reader)
    {
        var item = new FolderRemovalItem(
            ReadId(reader, StateSchema.FolderItemOperationId),
            ReadCount(reader, StateSchema.FolderItemOrdinal),
            ReadCount(reader, StateSchema.FolderItemGeneration),
            ReadEnum<FolderRemovalItem.ProvenanceKind>(reader, StateSchema.FolderItemProvenance),
            reader.GetString(StateSchema.FolderItemPath),
            reader.GetString(StateSchema.FolderItemPolicyKey),
            ReadOptionalEnvelope<Entry>(reader, StateSchema.FolderItemExpectedJson),
            ReadOptionalEnvelope<SyncedEntry>(reader, StateSchema.FolderItemSavedJson),
            ReadEnum<FolderVerdict>(reader, StateSchema.FolderItemVerdict),
            ReadBool(reader, StateSchema.FolderItemFollowUp),
            ReadEnum<FolderRemovalItem.FollowUpStatus>(reader, StateSchema.FolderItemFollowUpState),
            ReadBool(reader, StateSchema.FolderItemSelectedBranch),
            ReadEnum<FolderEffectKind>(reader, StateSchema.FolderItemEffectKind),
            ReadEnum<FolderRemovalItem.EffectStatus>(reader, StateSchema.FolderItemEffectState),
            ReadCount(reader, StateSchema.FolderItemEffectGeneration),
            ReadOptionalText(reader, StateSchema.FolderItemSubmissionId),
            reader.IsDBNull(StateSchema.FolderItemSubmissionMember)
                ? null : reader.GetInt32(StateSchema.FolderItemSubmissionMember),
            ReadOptionalEnvelope<FolderMemberResult>(reader, StateSchema.FolderItemEffectResultJson),
            ReadEnum<FolderRemovalItem.RecoveryPhase>(reader, StateSchema.FolderItemPhase),
            ReadOptionalEnvelope<Entry>(reader, StateSchema.FolderItemSelectedJson),
            ReadOptionalEnvelope<StageAllocation>(reader, StateSchema.FolderItemStageAllocationJson),
            ReadOptionalEnvelope<StageProof>(reader, StateSchema.FolderItemStageJson),
            ReadOptionalEnvelope<Entry>(reader, StateSchema.FolderItemResultJson),
            ReadOptionalEnum<TextKey>(reader, StateSchema.FolderItemCause));
        if (!EffectFactsAgree(item) || !RecoveryFactsAgree(item)) throw new InvalidDataException();
        return item;
    }

    /// <summary>Decodes one effect descriptor from <paramref name="firstColumn"/>. A provider-side conditional file
    /// must carry its revision precondition; a local conditional file carries no provider revision. A null side
    /// checks only the shape, for callers that hold no side.</summary>
    public static FolderRemovalEffect ReadFolderEffect(SqliteDataReader reader, int firstColumn, bool? providerSide)
    {
        var kind = ReadEnum<FolderEffectKind>(reader, firstColumn + StateSchema.ReadyEffectKind);
        var expected = ReadOptionalEnvelope<Entry>(reader, firstColumn + StateSchema.ReadyExpectedJson);
        if (!EffectMatches(kind, expected, providerSide)) throw new InvalidDataException();
        return new FolderRemovalEffect(
            ReadId(reader, firstColumn + StateSchema.ReadyOperationId),
            ReadCount(reader, firstColumn + StateSchema.ReadyOrdinal),
            ReadCount(reader, firstColumn + StateSchema.ReadyEffectGeneration),
            kind,
            expected);
    }

    /// <summary>Decodes one submission row and refuses facts its phase cannot have.</summary>
    public static FolderSubmission ReadFolderSubmission(SqliteDataReader reader)
    {
        var phase = ReadEnum<FolderSubmission.SubmissionPhase>(reader, StateSchema.SubmissionPhase);
        var jobId = ReadOptionalText(reader, StateSchema.SubmissionJobId);
        if (phase == FolderSubmission.SubmissionPhase.Accepted && jobId is null ||
            phase == FolderSubmission.SubmissionPhase.Armed && jobId is not null)
            throw new InvalidDataException();
        return new FolderSubmission(
            ReadId(reader, StateSchema.SubmissionId),
            reader.GetString(StateSchema.SubmissionPairId),
            ReadCount(reader, StateSchema.SubmissionGeneration),
            phase,
            jobId,
            ReadOptionalEnum<TextKey>(reader, StateSchema.SubmissionCause));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string? ReadOptionalText(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetString(column);

    private static string ReadId(SqliteDataReader reader, int column)
    {
        var id = reader.GetString(column);
        return id.Length == 0 ? throw new InvalidDataException() : id;
    }

    private static long ReadCount(SqliteDataReader reader, int column)
    {
        var count = reader.GetInt64(column);
        return count < 0 ? throw new InvalidDataException() : count;
    }

    private static Side ReadSide(SqliteDataReader reader, int column)
    {
        var side = reader.GetInt64(column);
        return side == (int)Side.Local ? Side.Local : side == (int)Side.Dropbox ? Side.Dropbox
            : throw new InvalidDataException();
    }

    private static string Canonical(object?[] key) => JsonSerializer.Serialize(key, Persisted);

    private static bool IsMalformed(Exception error) =>
        error is JsonException or ArgumentException or InvalidOperationException or FormatException;

    private static TResult DecodeStrict<TResult>(string json, Func<JsonElement, string, TResult> decode)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return decode(document.RootElement, json);
        }
        catch (Exception error) when (IsMalformed(error))
        {
            throw new InvalidDataException(null, error);
        }
    }

    // Null is an encoded absence; callers of a required fact refuse it, source evidence alone accepts it.
    internal static T? DecodeEnvelope<T>(string json) where T : class =>
        DecodeStrict(json, static (root, _) => DecodeValue<T>(root, unknownIds: false));

    // unknownIds admits a null IssueItem.Id, the stored shape of an item whose file id is unknown; nothing else relaxes.
    private static T? DecodeValue<T>(JsonElement root, bool unknownIds) where T : class
    {
        RequireShape(root, typeof(VersionedValue<object>));
        if (!root.GetProperty(nameof(VersionedValue<object>.Version)).TryGetInt32(out var version) ||
            version != (int)EncodingFormat.Current)
            throw new InvalidDataException();
        var value = root.GetProperty(nameof(VersionedValue<object>.Value));
        if (value.ValueKind == JsonValueKind.Null) return null;
        RequireShape(value, typeof(T), unknownIds);
        return value.Deserialize<T>(Persisted);
    }

    // Checked before typing the pair, so a bad binding keeps its own refusal rather than a generic JSON failure.
    private static void RequireBinding(JsonElement pair)
    {
        var bindings = pair.EnumerateObject().Where(static property => property.NameEquals(nameof(PairConfig.Roots)))
            .Select(static property => property.Value).ToArray();
        if (bindings.Length == 0 || bindings is [{ ValueKind: JsonValueKind.Null }]) return;
        try
        {
            if (bindings.Length > 1) throw new JsonException();
            RequireShape(bindings[0], typeof(RootBinding));
            if (bindings[0].Deserialize<RootBinding>(Persisted) is not { } binding || !binding.IsValid())
                throw new JsonException();
        }
        catch (Exception error) when (IsMalformed(error))
        {
            var pairId = pair.TryGetProperty(nameof(PairConfig.PairId), out var id) &&
                id.ValueKind == JsonValueKind.String ? id.GetString()! : string.Empty;
            throw new InvalidDataException(Text.Get(TextKey.StateRootBindingInvalid, (TextArg.Pair, pairId)), error);
        }
    }

    private static PendingRead ReadPendingNote(JsonElement note, string json)
    {
        if (note.ValueKind == JsonValueKind.Object && note.TryGetProperty(nameof(PendingEffect.Version), out _))
        {
            RequireShape(note, typeof(PendingEffect));
            return new CurrentPending(RequireCurrent(note.Deserialize<PendingEffect>(Persisted)!));
        }
        RequireShape(note, typeof(LegacyNote));
        var legacy = note.Deserialize<LegacyNote>(Persisted)!;
        return new LegacyPending(json, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(json))), legacy.Path,
            legacy.StagePath, legacy.Source);
    }

    // Encoding checks too, so a with-copy cannot persist a note the decoder would refuse.
    private static PendingEffect RequireCurrent(PendingEffect note)
    {
        var replacing = note.Kind == PendingKind.LocalReplace;
        if (note.Version != (int)EncodingFormat.Current || note.OperationId.Length == 0 || note.Generation < 0 ||
            note.Path.Length == 0 || replacing != (note.Source is not null) ||
            replacing != (note.Stage is not null) || note.Stage is { } stage && !IsComplete(stage) ||
            note.Victim is { } victim && !IsComplete(victim) ||
            !replacing && note.Phase is PendingPhase.PublishArmed or PendingPhase.Published or
                PendingPhase.DiscardArmed ||
            note.Phase == PendingPhase.Recycled && note.BinLocator is null ||
            note.Phase == PendingPhase.PermanentArmed && !note.PermanentAllowed ||
            note.Phase is PendingPhase.RestoreArmed or PendingPhase.WrongRestored && note.Victim is null)
            throw new InvalidDataException();
        return note;
    }

    private static bool IsIdentified(StageProof proof) =>
        proof.Path.Length != 0 && proof.VolumeId.Length != 0 && proof.FileId.Length != 0;

    private static bool IsComplete(StageProof? proof) =>
        proof is not null && IsIdentified(proof) && proof.Size >= 0 && proof.ContentHash.Length != 0;

    private static bool EffectMatches(FolderEffectKind kind, Entry? expected, bool? providerSide) => kind switch
    {
        FolderEffectKind.RecursiveFolder => expected is { IsFolder: true },
        FolderEffectKind.ConditionalFile => expected is { IsFolder: false } &&
            (providerSide is not { } side || (side ? expected.Revision.Length != 0 : expected.Revision.Length == 0)),
        FolderEffectKind.StateOnly => expected is null,
        _ => false
    };

    private static bool EffectFactsAgree(FolderRemovalItem item)
    {
        var none = item.EffectKind == FolderEffectKind.None;
        var onWire = item.EffectKind is FolderEffectKind.RecursiveFolder or FolderEffectKind.ConditionalFile;
        var linked = item.SubmissionId is not null;
        return item.FollowUp != (item.FollowUpState == FolderRemovalItem.FollowUpStatus.None) &&
            none == (item.EffectState == FolderRemovalItem.EffectStatus.None) &&
            // The item row is shared by both deleting sides and stores no side; the wire-effect decoder enforces
            // the revision rule where the side is known.
            (none || EffectMatches(item.EffectKind, item.Expected, providerSide: null)) &&
            (!onWire || item.Verdict == FolderVerdict.Removed) &&
            (item.Provenance == FolderRemovalItem.ProvenanceKind.Planned ||
                !item.FollowUp && !item.SelectedBranch && none) &&
            linked == (item.SubmissionMember is not null) &&
            (!linked || onWire && item.EffectState != FolderRemovalItem.EffectStatus.Ready) &&
            (linked || item.EffectState is not (FolderRemovalItem.EffectStatus.Armed or
                FolderRemovalItem.EffectStatus.Accepted)) &&
            ReceiptAgrees(item, linked);
    }

    // A receipt belongs to its own wire position and to the certainty the row claims.
    private static bool ReceiptAgrees(FolderRemovalItem item, bool linked)
    {
        var state = item.EffectState;
        if (item.EffectResult is not { } result) return !linked || state != FolderRemovalItem.EffectStatus.Verified;
        return linked && result.Ordinal == item.SubmissionMember &&
            (state == FolderRemovalItem.EffectStatus.Settled && result is not FolderMemberResult.Unknown ||
            state == result switch
            {
                FolderMemberResult.Receipt => FolderRemovalItem.EffectStatus.Verified,
                FolderMemberResult.KnownNoEffect => FolderRemovalItem.EffectStatus.NoEffect,
                _ => FolderRemovalItem.EffectStatus.Unknown
            });
    }

    // Only the facts of the current phase are stored, and StageArmed's proof is identity without content.
    private static bool RecoveryFactsAgree(FolderRemovalItem item)
    {
        if (item.Stage is not null && (item.StageAllocation is null || !IsIdentified(item.Stage)) ||
            item.StageAllocation is not null && item.Selected is null)
            return false;
        var held = item.Stage is not null ? Custody.Staged
            : item.StageAllocation is not null ? Custody.Allocated
            : item.Selected is not null ? Custody.Selected : Custody.None;
        return item.Phase switch
        {
            FolderRemovalItem.RecoveryPhase.Planned or FolderRemovalItem.RecoveryPhase.DeleteArmed or
                FolderRemovalItem.RecoveryPhase.Deleted => held == Custody.None && item.Result is null,
            FolderRemovalItem.RecoveryPhase.RecoverySelected => held == Custody.Selected && item.Result is null,
            FolderRemovalItem.RecoveryPhase.AllocateArmed => held == Custody.Allocated && item.Result is null,
            FolderRemovalItem.RecoveryPhase.StageArmed => held == Custody.Staged && item.Result is null &&
                item.Stage!.Size == 0 && item.Stage.ContentHash.Length == 0,
            FolderRemovalItem.RecoveryPhase.Staged or FolderRemovalItem.RecoveryPhase.PublishArmed =>
                IsComplete(item.Stage) && item.Result is null,
            FolderRemovalItem.RecoveryPhase.Published => IsComplete(item.Stage) && item.Result is not null,
            FolderRemovalItem.RecoveryPhase.DiscardArmed => held == Custody.Staged,
            FolderRemovalItem.RecoveryPhase.Settled => held <= Custody.Selected,
            _ => true
        };
    }

    // System.Text.Json on this target binds a missing constructor field to its default, lets the last duplicate win
    // and ignores nullable annotations, so presence, uniqueness, nullness and exact enum names are checked here.
    private static void RequireShape(JsonElement value, Type type, bool unknownIds = false)
    {
        if (type.IsEnum)
        {
            if (value.ValueKind != JsonValueKind.String || !Enum.IsDefined(type, value.GetString()!))
                throw new JsonException();
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            RequireItems(value, type.GetGenericArguments()[0], unknownIds);
            return;
        }
        if (!type.IsClass || type == typeof(string) || type == typeof(object)) return;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var members = ShapeOf(typeof(FolderMemberResult).IsAssignableFrom(type) ? typeof(MemberReceipt) : type);
        var seen = new bool[members.Length];
        foreach (var property in value.EnumerateObject())
        {
            var index = IndexOf(members, property);
            if (index == members.Length || seen[index]) throw new JsonException();
            seen[index] = true;
            if (property.Value.ValueKind != JsonValueKind.Null) RequireShape(property.Value, members[index].Type, unknownIds);
            else if (!members[index].AllowsNull &&
                !(unknownIds && type == typeof(IssueItem) && members[index].Name == nameof(IssueItem.Id)))
                throw new JsonException();
        }
        for (var index = 0; index < members.Length; index++)
            if (!seen[index] && !members[index].Optional) throw new JsonException();
    }

    private static void RequireItems(JsonElement value, Type itemType, bool unknownIds)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new JsonException();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null) throw new JsonException();
            RequireShape(item, itemType, unknownIds);
        }
    }

    // The member's position, or the array length when the name is not part of the shape.
    private static int IndexOf(Member[] members, JsonProperty property)
    {
        var index = 0;
        while (index < members.Length && !property.NameEquals(members[index].Name)) index++;
        return index;
    }

    private static Member[] ShapeOf(Type type) => Shapes.GetOrAdd(type, static key =>
    {
        var nullability = new NullabilityInfoContext();
        return key.GetConstructors().MaxBy(static constructor => constructor.GetParameters().Length)!.GetParameters()
            .Select(parameter => new Member(parameter.Name!,
                Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType,
                parameter.HasDefaultValue, nullability.Create(parameter).ReadState != NullabilityState.NotNull))
            .ToArray();
    });

    private sealed record Member(string Name, Type Type, bool Optional, bool AllowsNull);

    /// <summary>The frozen discriminator of envelopes, pending notes and canonical keys.</summary>
    private enum EncodingFormat { Current = 1 }

    /// <summary>How far one member's recovery bytes have come; each stage needs the one before it.</summary>
    private enum Custody { None, Selected, Allocated, Staged }

    private enum MemberKind { Receipt, KnownNoEffect, Unknown }

    private sealed record VersionedValue<T>(int Version, T? Value);

    private sealed record LegacyNote(string Path, string StagePath, Entry Source);

    private sealed record MemberReceipt(MemberKind Kind, int Ordinal, Entry? Entry, TextKey? Cause);

    /// <summary>The closed discriminator for a member result; its Contracts type declares no polymorphism.</summary>
    private sealed class MemberResultConverter : JsonConverter<FolderMemberResult>
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeof(FolderMemberResult).IsAssignableFrom(typeToConvert);

        public override FolderMemberResult Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<MemberReceipt>(ref reader, options) switch
            {
                { Kind: MemberKind.Receipt, Entry: { } entry, Cause: null } receipt =>
                    new FolderMemberResult.Receipt(receipt.Ordinal, entry),
                { Kind: MemberKind.KnownNoEffect, Entry: null } noEffect =>
                    new FolderMemberResult.KnownNoEffect(noEffect.Ordinal, noEffect.Cause),
                { Kind: MemberKind.Unknown, Entry: null } unknown =>
                    new FolderMemberResult.Unknown(unknown.Ordinal, unknown.Cause),
                _ => throw new JsonException()
            };

        public override void Write(Utf8JsonWriter writer, FolderMemberResult value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value switch
            {
                FolderMemberResult.Receipt receipt =>
                    new MemberReceipt(MemberKind.Receipt, receipt.Ordinal, receipt.Entry, null),
                FolderMemberResult.KnownNoEffect noEffect =>
                    new MemberReceipt(MemberKind.KnownNoEffect, noEffect.Ordinal, null, noEffect.Cause),
                FolderMemberResult.Unknown unknown =>
                    new MemberReceipt(MemberKind.Unknown, unknown.Ordinal, null, unknown.Cause),
                _ => throw new JsonException()
            }, options);
    }
}
