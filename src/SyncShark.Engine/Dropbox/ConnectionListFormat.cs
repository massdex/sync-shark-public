using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox connection list file format: its canonical array line, the 0.3.95 slots and append log,
/// and the value the format projects to connection ids. Pure functions over the bytes a root holds.</summary>
internal static class ConnectionListFormat
{
    private const int AppendSchemaVersion = 1;
    // PhysicalMaximumRecords in the released 0.3.95 AppendOnlyJsonStore format, not a writer tuning parameter.
    private const int MaximumAppendRecords = 100;
    private const string SetOperation = "set";
    private const string DeleteOperation = "delete";
    private const string RecoveryOperation = "recovery";

    // The predecessor's default typed-read options.
    internal static readonly JsonSerializerOptions JsonOptions = new();

    // --- 0.3.95 record shapes, ported so the framework decodes exactly as the predecessor did --------------------

    internal sealed record MutableJsonValue<TValue>(bool Present, TValue? Value);

    internal sealed record SlotRecord<TValue>(int SchemaVersion, long Generation, TValue? Value, string? PayloadSha256);

    private sealed record AppendRecord<TValue>(int SchemaVersion, string? Operation, TValue? Value,
        string? PayloadSha256, RecoveryState? Recovery);

    private sealed record RecoveryState(string? EvidenceFile, string? OriginalSha256, int ValidPrefixByteLength,
        int BadByteLength, string? BadSha256, string? Outcome);

    internal sealed record ProviderCapabilityFlags(
        bool ContentRead,
        bool ContentWrite,
        bool MetadataRead,
        bool MetadataWrite,
        bool SharingRead,
        bool SharingWrite,
        bool StableObjectIds,
        bool DeltaCursor,
        bool NativeMove,
        bool Revisions,
        bool DeletedItems,
        bool ProgrammaticRewind,
        bool FileRequestsRead = false,
        bool FileRequestsWrite = false,
        bool NativeDocuments = false,
        bool Shortcuts = false,
        bool SharedDrives = false,
        bool ConditionalWrites = true);

    internal sealed record ProviderConnectionRecord(
        string ConnectionId,
        string Provider,
        string AccountId,
        string RootNamespaceId,
        string HomeNamespaceId,
        string DisplayName,
        string Email,
        string AccountType,
        string CredentialReference,
        bool IsActive,
        ProviderCapabilityFlags Capabilities);

    internal sealed record DropboxConnectionBinding(
        string ClientId,
        string AccountId,
        string RootNamespaceId,
        string HomeNamespaceId,
        string ContentAccess,
        IReadOnlyList<string> Scopes,
        string ConnectionId = "",
        string DisplayName = "",
        string Email = "",
        string AccountType = "",
        DateTimeOffset? ConnectedUtc = null,
        DateTimeOffset? LastVerifiedUtc = null,
        string CredentialReference = "",
        IReadOnlyList<ProviderConnectionRecord>? RetainedConnections = null,
        IReadOnlyList<DropboxConnectionBinding>? AdditionalConnections = null);

    internal enum ListShape { Absent, Canonical, Append }

    internal static ListShape ListForm(byte[] bytes)
    {
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
    internal static IReadOnlyList<string> CanonicalIds(byte[] bytes)
    {
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
    internal static IReadOnlyList<string> AppendSet(byte[] bytes)
    {
        DropboxConnectionBinding? winner = null;
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
                if (!TryApplyLegacy(trimmed.Span, document.RootElement, ref winner)) break;
            }
            if (++records > MaximumAppendRecords) throw InvalidList();
        }
        return winner is not null ? ProjectBinding(winner) : Array.Empty<string>();
    }

    private static bool TryApplyLegacy(ReadOnlySpan<byte> bytes, JsonElement record, ref DropboxConnectionBinding? winner)
    {
        AppendRecord<DropboxConnectionBinding>? decoded;
        try { decoded = JsonSerializer.Deserialize<AppendRecord<DropboxConnectionBinding>>(bytes, JsonOptions); }
        catch (JsonException) { return false; }
        if (decoded is null || decoded.SchemaVersion != AppendSchemaVersion || decoded.PayloadSha256 is null ||
            string.IsNullOrEmpty(decoded.Operation))
            return false;
        var kind = decoded.Operation;
        if (kind == DeleteOperation)
        {
            if (!HashMatches(decoded.PayloadSha256, Encoding.UTF8.GetBytes(DeleteOperation))) return false;
            winner = null;
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
        return true;
    }

    internal static bool HashMatches(string claimed, byte[] payload)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(claimed), SHA256.HashData(payload));
        }
        catch (FormatException) { return false; }
    }

    private static ReadOnlyMemory<byte> TrimRecord(ReadOnlyMemory<byte> line) =>
        line.Length > 0 && line.Span[^1] == (byte)'\r' ? line[..^1] : line;

    private static IEnumerable<ReadOnlyMemory<byte>> TerminatedLines(byte[] bytes)
    {
        var start = 0;
        int end;
        while ((end = Array.IndexOf(bytes, (byte)'\n', start)) >= 0)
        {
            yield return bytes.AsMemory(start, end - start);
            start = end + 1;
        }
    }

    /// <summary>Projects the selected winner to ids once: its explicit trimmed id or the predecessor's derived account
    /// id, then the same for each additional connection.</summary>
    internal static IReadOnlyList<string> ProjectBinding(DropboxConnectionBinding binding)
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
        if (binding.RetainedConnections?.Any(item => item is null) == true) throw InvalidList();
        if (!string.IsNullOrWhiteSpace(binding.ConnectionId))
        {
            ids.Add(ValidatedConnectionId(binding.ConnectionId.Trim()));
            return;
        }
        if (!string.IsNullOrWhiteSpace(binding.AccountId))
        {
            ids.Add(Connections.CreateConnectionId(binding.AccountId));
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

    internal static InvalidDataException InvalidList() => new(Text.Get(TextKey.RootDropboxIdentityInvalid));
}
