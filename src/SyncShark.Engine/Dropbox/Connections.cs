using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox connection list at <c>&lt;ProductRoot&gt;/state/dropbox-connection.jsonl</c>, as in 0.3.95.
/// It holds only connection ids; the account's saved root identity lives with each pair, so no credential reference
/// is stored or returned here. One canonical representation covers the whole list: one JSON array line of ids,
/// including <c>[]</c> for the empty list; a zero-byte, blank-line or append-record JSONL is a 0.3.95 legacy log.
/// The legacy value is decoded by the framework into the ported 0.3.95 record shapes (slots first, otherwise the
/// append log) and only its winner is projected to ids.</summary>
public sealed class Connections
{
    private const string StateFolder = "state";
    private const string FileName = "dropbox-connection.jsonl";
    private const string SlotA = "dropbox-connection-a.json";
    private const string SlotB = "dropbox-connection-b.json";
    private const int SlotSchemaVersion = 1;
    private const int AppendSchemaVersion = 1;
    private const int MaximumStateBytes = 1024 * 1024;
    private const int MaximumAppendRecords = 100;
    private const string ConnectionPrefix = "connection-";
    private const int ConnectionHashLength = 24;
    private const string SetOperation = "set";
    private const string DeleteOperation = "delete";
    private const string RecoveryOperation = "recovery";

    // The predecessor's default typed-read options.
    private static readonly JsonSerializerOptions JsonOptions = new();

    // --- 0.3.95 record shapes, ported so the framework decodes exactly as the predecessor did --------------------

    private sealed record MutableJsonValue<TValue>(bool Present, TValue? Value);

    private sealed record SlotRecord<TValue>(int SchemaVersion, long Generation, TValue? Value, string? PayloadSha256);

    private sealed record AppendRecord<TValue>(int SchemaVersion, string? Operation, TValue? Value,
        string? PayloadSha256, RecoveryState? Recovery);

    private sealed record RecoveryState(string? EvidenceFile, string? OriginalSha256, int ValidPrefixByteLength,
        int BadByteLength, string? BadSha256, string? Outcome);

    private sealed record ProviderCapabilityFlags(bool ContentRead, bool ContentWrite, bool MetadataRead,
        bool MetadataWrite, bool SharingRead, bool SharingWrite, bool StableObjectIds, bool DeltaCursor,
        bool NativeMove, bool Revisions, bool DeletedItems, bool ProgrammaticRewind, bool FileRequestsRead,
        bool FileRequestsWrite, bool NativeDocuments, bool Shortcuts, bool SharedDrives, bool ConditionalWrites);

    private sealed record ProviderConnectionRecord(string? ConnectionId, string? Provider, string? AccountId,
        string? RootNamespaceId, string? HomeNamespaceId, string? DisplayName, string? Email, string? AccountType,
        string? CredentialReference, bool IsActive, ProviderCapabilityFlags? Capabilities);

    private sealed record DropboxConnectionBinding(string? ClientId, string? AccountId, string? RootNamespaceId,
        string? HomeNamespaceId, string? ContentAccess, string[]? Scopes, string? ConnectionId, string? DisplayName,
        string? Email, string? AccountType, DateTimeOffset? ConnectedUtc, DateTimeOffset? LastVerifiedUtc,
        string? CredentialReference, ProviderConnectionRecord[]? RetainedConnections,
        DropboxConnectionBinding[]? AdditionalConnections);

    /// <summary>The stable connection id of a Dropbox account: <c>connection-</c> and 24 lowercase hex digits of its
    /// domain-separated account hash, as 0.3.95 created it.</summary>
    public static string CreateConnectionId(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId, nameof(accountId));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("dropbox\n" + accountId));
        return ConnectionPrefix + Convert.ToHexString(hash).ToLowerInvariant()[..ConnectionHashLength];
    }

    /// <summary>The product's present connection ids: the canonical array line when present, otherwise the 0.3.95
    /// authoritative value of this root (slots first, then the typed append log). A missing list is empty; a malformed
    /// one refuses.</summary>
    public IReadOnlyList<string> Read(string productRoot) => Resolve(productRoot).Ids;

    /// <summary>Verify the source's authoritative 0.3.95 value and the target before the store switch. In place, the
    /// root's own JSONL is evidence and is never overwritten, and the value Read will expose must equal the authoritative
    /// source set (refusing an unresolved divergence). For distinct roots the target is created only when absent (copying
    /// the authoritative append log's bytes when that log supplies them); an existing target must carry the same set and,
    /// when it is an append list with an authoritative append source, the same bytes. A slot-only source has no
    /// authoritative append bytes, so only its set is compared (the slot is authoritative). A same-set canonical target
    /// with different bytes is accepted and neither side is overwritten (R47); the migration owner reports it once via
    /// the existing notice path. On refusal remove only this attempt's files and throw before database publication; on
    /// success return cleanup for a failed switch, or null when nothing was created.</summary>
    public Action? Verify(string sourceRoot, string productRoot, IReadOnlyList<string> requiredConnectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot, nameof(sourceRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(requiredConnectionIds, nameof(requiredConnectionIds));
        var source = FilePath(sourceRoot);
        var target = FilePath(productRoot);
        var sameRoot = SameRoot(sourceRoot, productRoot);
        // One observation of this root when it is both source and product (no canonical candidate) is reused.
        (ListShape Form, IReadOnlyList<string> Ids)? presented = sameRoot ? Resolve(productRoot) : null;
        var sourceSet = presented is { Form: not ListShape.Canonical } observed ? observed.Ids : SourceSet(sourceRoot);
        var slotsAuthoritative = SlotsExist(sourceRoot);
        var appendSuppliesBytes = !slotsAuthoritative && File.Exists(source);
        var created = false;
        IReadOnlyList<string> actual;
        try
        {
            if (sameRoot)
            {
                var resolved = presented!.Value;
                if (File.Exists(target) || slotsAuthoritative)
                {
                    // The value Read will expose must be the authoritative one; a divergence refuses before publication.
                    if (!resolved.Ids.SequenceEqual(sourceSet, StringComparer.Ordinal))
                        throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
                    actual = resolved.Ids;
                }
                else actual = sourceSet;
            }
            else
            {
                var resolved = Resolve(productRoot);
                var targetPresent = resolved.Form != ListShape.Absent || SlotsExist(productRoot);
                if (targetPresent)
                {
                    if (!resolved.Ids.SequenceEqual(sourceSet, StringComparer.Ordinal))
                        throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
                    if (resolved.Form == ListShape.Append && appendSuppliesBytes && !SameBytesBounded(source, target))
                        throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
                    actual = resolved.Ids;
                }
                else if (sourceSet.Count > 0 || slotsAuthoritative || File.Exists(source))
                {
                    var published = appendSuppliesBytes
                        ? Publish(target, output => CopyBounded(source, output))
                        : Publish(target, output => WriteIds(output, sourceSet));
                    if (!published) throw new IOException();
                    created = true;
                    actual = Resolve(productRoot).Ids;
                }
                else actual = sourceSet;
            }
            foreach (var required in requiredConnectionIds)
                if (!actual.Contains(required, StringComparer.Ordinal))
                    throw new InvalidDataException(Text.Get(TextKey.AccountMissing, (TextArg.Account, required)));
            return created ? () => TryDelete(target) : null;
        }
        catch
        {
            if (created) TryDelete(target);
            throw;
        }
    }

    /// <summary>Replaces the product's list atomically with its one canonical array line.</summary>
    public void Save(string productRoot, IReadOnlyList<string> connections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(connections, nameof(connections));
        var ids = connections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var id in ids) _ = CredentialStore.AccountTarget(id);
        if (!Publish(FilePath(productRoot), output => WriteIds(output, ids), overwrite: true)) throw new IOException();
    }

    private static string FilePath(string productRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        return Path.Combine(productRoot, StateFolder, FileName);
    }

    private static bool SameRoot(string sourceRoot, string productRoot) =>
        string.Equals(FullRoot(sourceRoot), FullRoot(productRoot), StringComparison.OrdinalIgnoreCase);

    private static string FullRoot(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string StagePath(string target) =>
        // A fresh stage id (the established 32-hex id form) keeps an abandoned same-second stage from blocking a retry.
        target + "." + ResourceNames.NewId() + ".new";

    /// <summary>The one attempt-publication path: write this attempt's own stage, then move it create-only (or replace).</summary>
    private static bool Publish(string target, Action<FileStream> write, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stage = StagePath(target);
        FileStream output;
        try { output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) { return false; }
        try
        {
            using (output)
            {
                write(output);
                output.Flush(true);
            }
            File.Move(stage, target, overwrite);
            return true;
        }
        catch
        {
            TryDelete(stage);
            throw;
        }
    }

    private static void WriteIds(FileStream output, IReadOnlyList<string> ids)
    {
        // One representation for every list: one JSON array line, [] for the empty list.
        output.Write(JsonSerializer.SerializeToUtf8Bytes(ids.ToArray()));
        output.WriteByte((byte)'\n');
    }

    private static void CopyBounded(string source, FileStream output)
    {
        if (new FileInfo(source).Length > MaximumStateBytes) throw InvalidList();
        output.Write(File.ReadAllBytes(source));
    }

    private static bool SameBytesBounded(string left, string right)
    {
        if (new FileInfo(left).Length > MaximumStateBytes || new FileInfo(right).Length > MaximumStateBytes)
            throw InvalidList();
        return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    // --- The product's list or a not-yet-upgraded root's 0.3.95 value, resolved once -----------------------------

    private enum ListShape { Absent, Canonical, Append }

    private static (ListShape Form, IReadOnlyList<string> Ids) Resolve(string productRoot)
    {
        var path = FilePath(productRoot);
        var form = ListForm(path);
        if (form == ListShape.Canonical) return (form, CanonicalIds(path));
        if (SlotsExist(productRoot)) return (form, SlotSet(productRoot));
        return (form, AppendSet(path));
    }

    /// <summary>The source's authoritative 0.3.95 value: successor slots when either exists, else the append log.</summary>
    private static IReadOnlyList<string> SourceSet(string sourceRoot) =>
        SlotsExist(sourceRoot) ? SlotSet(sourceRoot) : AppendSet(FilePath(sourceRoot));

    private static bool SlotsExist(string productRoot)
    {
        var state = Path.Combine(productRoot, StateFolder);
        return File.Exists(Path.Combine(state, SlotA)) || File.Exists(Path.Combine(state, SlotB));
    }

    private static IReadOnlyList<string> SlotSet(string productRoot)
    {
        var state = Path.Combine(productRoot, StateFolder);
        var a = ReadSlot(Path.Combine(state, SlotA));
        var b = ReadSlot(Path.Combine(state, SlotB));
        // A successor exists, so the legacy log is never consulted; both invalid refuses rather than roll back.
        var selected = SelectSlot(a, b) ?? throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        return selected.Present && selected.Value is not null ? ProjectBinding(selected.Value) : Array.Empty<string>();
    }

    private sealed record Slot(bool Valid, long Generation, bool Present, DropboxConnectionBinding? Value);

    private static Slot ReadSlot(string path)
    {
        var invalid = new Slot(false, 0, false, null);
        if (!File.Exists(path)) return invalid;
        if (new FileInfo(path).Length is 0 or > MaximumStateBytes) return invalid;
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("Value", out var rawValue) || rawValue.ValueKind == JsonValueKind.Null)
                return invalid;
            var record = JsonSerializer.Deserialize<SlotRecord<MutableJsonValue<DropboxConnectionBinding>>>(bytes, JsonOptions);
            if (record is null || record.SchemaVersion != SlotSchemaVersion || record.Generation <= 0 ||
                record.Value is null || record.PayloadSha256 is null)
                return invalid;
            if (!HashMatches(record.PayloadSha256, Encoding.UTF8.GetBytes(rawValue.GetRawText()))) return invalid;
            // An omitted Present defaults to false; Present:true with a null Value is valid and yields no membership.
            return new Slot(true, record.Generation, record.Value.Present, record.Value.Value);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return invalid;
        }
    }

    private static Slot? SelectSlot(Slot a, Slot b)
    {
        if (a.Valid && b.Valid) return a.Generation >= b.Generation ? a : b;
        if (a.Valid) return a;
        if (b.Valid) return b;
        return null;
    }

    private static ListShape ListForm(string path)
    {
        if (!File.Exists(path)) return ListShape.Absent;
        if (new FileInfo(path).Length > MaximumStateBytes) return ListShape.Append;
        var bytes = File.ReadAllBytes(path);
        // Only one JSON array line is canonical; legacy input (append records, a blank or zero-byte log) is not.
        foreach (var line in TerminatedLines(bytes))
        {
            var trimmed = TrimRecord(line);
            if (trimmed.Length == 0) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                return document.RootElement.ValueKind == JsonValueKind.Array ? ListShape.Canonical : ListShape.Append;
            }
            catch (JsonException) { return ListShape.Append; }
        }
        return ListShape.Append;
    }

    /// <summary>Our canonical list: exactly one JSON array line of string ids; a partial tail or any other value refuses.</summary>
    private static IReadOnlyList<string> CanonicalIds(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n') throw InvalidList();
        var ids = new List<string>();
        var arrays = 0;
        foreach (var line in TerminatedLines(bytes))
        {
            var trimmed = TrimRecord(line);
            if (trimmed.Length == 0) continue;
            if (++arrays > 1) throw InvalidList();
            JsonDocument document;
            try { document = JsonDocument.Parse(trimmed); }
            catch (JsonException) { throw InvalidList(); }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidList();
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) throw InvalidList();
                    ids.Add(ValidatedConnectionId(item.GetString()!));
                }
            }
        }
        if (arrays != 1) throw InvalidList();
        return SortedDistinct(ids);
    }

    /// <summary>The 0.3.95 append log: only a wholly empty record is skipped; the framework decodes each record into the
    /// ported shape and the scan stops at the first undecodable/invalid one, counting only applied records. The valid
    /// prefix selects a winner, which is projected once; a superseded set never projects its own ids.</summary>
    private static IReadOnlyList<string> AppendSet(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        if (new FileInfo(path).Length > MaximumStateBytes) throw InvalidList();
        var bytes = File.ReadAllBytes(path);
        DropboxConnectionBinding? winner = null;
        var hasWinner = false;
        var records = 0;
        foreach (var line in TerminatedLines(bytes))
        {
            if (line.Length == 0) continue; // the predecessor skips a wholly empty record only
            var trimmed = TrimRecord(line);
            JsonDocument document;
            try { document = JsonDocument.Parse(trimmed); }
            catch (JsonException) { break; }
            using (document)
            {
                if (!TryApplyLegacy(document.RootElement, ref winner, ref hasWinner)) break;
            }
            if (++records > MaximumAppendRecords) throw InvalidList();
        }
        return hasWinner && winner is not null ? ProjectBinding(winner) : Array.Empty<string>();
    }

    private static bool TryApplyLegacy(JsonElement record, ref DropboxConnectionBinding? winner, ref bool hasWinner)
    {
        AppendRecord<DropboxConnectionBinding>? decoded;
        try { decoded = JsonSerializer.Deserialize<AppendRecord<DropboxConnectionBinding>>(record.GetRawText(), JsonOptions); }
        catch (JsonException) { return false; }
        if (decoded is null || decoded.SchemaVersion != AppendSchemaVersion || decoded.PayloadSha256 is null ||
            string.IsNullOrEmpty(decoded.Operation))
            return false;
        var kind = decoded.Operation;
        if (kind == DeleteOperation)
        {
            if (!HashMatches(decoded.PayloadSha256, Encoding.UTF8.GetBytes(DeleteOperation))) return false;
            winner = null;
            hasWinner = false;
            return true;
        }
        if (kind == RecoveryOperation)
        {
            // The predecessor's recovery operation requires its named evidence file; its typed shape is already decoded.
            if (decoded.Recovery is null || string.IsNullOrWhiteSpace(decoded.Recovery.EvidenceFile)) return false;
            if (!record.TryGetProperty("Recovery", out var rawRecovery) || rawRecovery.ValueKind == JsonValueKind.Null)
                return false;
            return HashMatches(decoded.PayloadSha256, Encoding.UTF8.GetBytes(rawRecovery.GetRawText()));
        }
        if (kind != SetOperation) return false;
        if (decoded.Value is null) return false;
        if (!record.TryGetProperty("Value", out var rawValue) || rawValue.ValueKind == JsonValueKind.Null) return false;
        if (!HashMatches(decoded.PayloadSha256, Encoding.UTF8.GetBytes(rawValue.GetRawText()))) return false;
        winner = decoded.Value;
        hasWinner = true;
        return true;
    }

    private static bool HashMatches(string claimed, byte[] payload)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(claimed), SHA256.HashData(payload));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] TrimRecord(byte[] line) =>
        line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;

    private static List<byte[]> TerminatedLines(byte[] bytes)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            lines.Add(bytes.AsSpan(start, index - start).ToArray());
            start = index + 1;
        }
        return lines;
    }

    /// <summary>Projects the selected winner to ids once: its explicit trimmed id or the predecessor's derived account
    /// id, then the same for each additional connection.</summary>
    private static IReadOnlyList<string> ProjectBinding(DropboxConnectionBinding binding)
    {
        var ids = new List<string>();
        AddBindingId(binding, ids);
        if (binding.AdditionalConnections is { } additional)
            foreach (var connection in additional)
            {
                if (connection is null) throw InvalidList();
                AddBindingId(connection, ids);
            }
        return SortedDistinct(ids);
    }

    private static void AddBindingId(DropboxConnectionBinding binding, List<string> ids)
    {
        if (!string.IsNullOrWhiteSpace(binding.ConnectionId))
        {
            ids.Add(ValidatedConnectionId(binding.ConnectionId.Trim()));
            return;
        }
        if (!string.IsNullOrWhiteSpace(binding.AccountId))
        {
            ids.Add(CreateConnectionId(binding.AccountId));
            return;
        }
        throw InvalidList();
    }

    private static string ValidatedConnectionId(string id)
    {
        _ = CredentialStore.AccountTarget(id);
        return id;
    }

    private static string[] SortedDistinct(List<string> ids) =>
        ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static InvalidDataException InvalidList() => new(Text.Get(TextKey.RootDropboxIdentityInvalid));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
