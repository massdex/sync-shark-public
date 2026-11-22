using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox connection list at <c>&lt;ProductRoot&gt;/state/dropbox-connection.jsonl</c>, as in 0.3.95.
/// It holds only connection ids; the account's saved root identity lives with each pair, so no credential reference
/// is stored or returned here. One canonical representation covers the whole list: one JSON array line of ids,
/// including <c>[]</c> for the empty list; a zero-byte, blank-line or append-record JSONL is a 0.3.95 legacy log.
/// Migration resolves 0.3.95's authoritative value (slots first, otherwise the typed append log) and, in place,
/// treats the root's own JSONL as source evidence.</summary>
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
                    if (!(resolved.Form == ListShape.Append && !appendSuppliesBytes) &&
                        !resolved.Ids.SequenceEqual(sourceSet, StringComparer.Ordinal))
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
        target + "." + System.DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + ".new";

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
        var ids = new List<string>();
        if (selected.Present) ProjectBinding(selected.Value, ids);
        return SortedDistinct(ids);
    }

    private sealed record Slot(bool Valid, long Generation, bool Present, JsonElement Value);

    private static Slot ReadSlot(string path)
    {
        var invalid = new Slot(false, 0, false, default);
        if (!File.Exists(path)) return invalid;
        if (new FileInfo(path).Length is 0 or > MaximumStateBytes) return invalid;
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                schema.GetInt32() != SlotSchemaVersion ||
                !root.TryGetProperty("Generation", out var generationElement) ||
                generationElement.ValueKind != JsonValueKind.Number ||
                !generationElement.TryGetInt64(out var generation) || generation <= 0 ||
                !root.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("PayloadSha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash.GetString()!),
                    SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText()))) ||
                !value.TryGetProperty("Present", out var present) ||
                present.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return invalid;
            if (!present.GetBoolean())
            {
                // The predecessor's typed read still decodes a supplied value; a wrong-typed one invalidates this slot.
                return value.TryGetProperty("Value", out var tombstone) && tombstone.ValueKind != JsonValueKind.Null &&
                       !ValidBinding(tombstone)
                    ? invalid
                    : new Slot(true, generation, false, default);
            }
            // The predecessor's typed payload must be structurally valid before this generation is considered.
            return value.TryGetProperty("Value", out var binding) && ValidBinding(binding)
                ? new Slot(true, generation, true, binding.Clone())
                : invalid;
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

    /// <summary>The 0.3.95 append log, in the predecessor's order: only a wholly empty record is skipped; every other
    /// record is structurally validated and the scan stops at the first invalid one, counting only applied records.</summary>
    private static IReadOnlyList<string> AppendSet(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        if (new FileInfo(path).Length > MaximumStateBytes) throw InvalidList();
        var bytes = File.ReadAllBytes(path);
        var ids = new List<string>();
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
                if (!TryAddLegacyRecord(document.RootElement, ids)) break;
            }
            if (++records > MaximumAppendRecords) throw InvalidList();
        }
        return SortedDistinct(ids);
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

    private static bool TryAddLegacyRecord(JsonElement record, List<string> ids)
    {
        try
        {
            if (record.ValueKind != JsonValueKind.Object ||
                !record.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                schema.GetInt32() != AppendSchemaVersion ||
                !record.TryGetProperty("Operation", out var operation) || operation.ValueKind != JsonValueKind.String ||
                !record.TryGetProperty("PayloadSha256", out var hash) || hash.ValueKind != JsonValueKind.String)
                return false;
            var kind = operation.GetString();
            // The predecessor typed-decodes the whole envelope, including supplied Value and Recovery fields.
            if (record.TryGetProperty("Value", out var supplied) && supplied.ValueKind != JsonValueKind.Null &&
                !ValidBinding(supplied))
                return false;
            if (record.TryGetProperty("Recovery", out var suppliedRecovery) &&
                suppliedRecovery.ValueKind != JsonValueKind.Null && !ValidRecovery(suppliedRecovery))
                return false;
            byte[] claimed;
            try { claimed = Convert.FromHexString(hash.GetString()!); }
            catch (FormatException) { return false; }
            if (kind == DeleteOperation)
            {
                if (!CryptographicOperations.FixedTimeEquals(claimed, SHA256.HashData(Encoding.UTF8.GetBytes(DeleteOperation))))
                    return false;
                ids.Clear();
                return true;
            }
            JsonElement payload;
            if (kind == SetOperation)
            {
                if (!record.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Object) return false;
                payload = value;
            }
            else if (kind == RecoveryOperation)
            {
                if (!record.TryGetProperty("Recovery", out var recovery) || !ValidRecovery(recovery)) return false;
                payload = recovery;
            }
            else return false;
            if (!CryptographicOperations.FixedTimeEquals(claimed, SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText()))))
                return false;
            if (kind == RecoveryOperation) return true;
            ids.Clear();
            ProjectBinding(payload, ids);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The predecessor's typed payload shape: field types must permit its typed read before acceptance.</summary>
    private static bool ValidBinding(JsonElement binding)
    {
        if (binding.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in TypedStringFields)
            if (!StringOrNull(binding, name)) return false;
        foreach (var name in TypedTimeFields)
            if (!TimeOrNull(binding, name)) return false;
        if (!StringArrayOrNull(binding, "Scopes")) return false;
        if (!BindingsOrNull(binding, "AdditionalConnections")) return false;
        return RetainedOrNull(binding, "RetainedConnections");
    }

    private static readonly string[] TypedStringFields =
    [
        "ClientId", "AccountId", "RootNamespaceId", "HomeNamespaceId", "ContentAccess",
        "ConnectionId", "DisplayName", "Email", "AccountType", "CredentialReference"
    ];

    private static readonly string[] TypedTimeFields = ["ConnectedUtc", "LastVerifiedUtc"];

    private static bool StringOrNull(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) ||
        value.ValueKind is JsonValueKind.String or JsonValueKind.Null;

    private static bool TimeOrNull(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>The predecessor's typed recovery record: its named evidence and integer byte counts.</summary>
    private static bool ValidRecovery(JsonElement recovery)
    {
        if (recovery.ValueKind != JsonValueKind.Object) return false;
        if (!recovery.TryGetProperty("EvidenceFile", out var evidence) || evidence.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(evidence.GetString()))
            return false;
        if (!recovery.TryGetProperty("OriginalSha256", out var original) || original.ValueKind != JsonValueKind.String)
            return false;
        if (!recovery.TryGetProperty("ValidPrefixByteLength", out var prefix) || !prefix.TryGetInt32(out _)) return false;
        if (!recovery.TryGetProperty("BadByteLength", out var badLength) || !badLength.TryGetInt32(out _)) return false;
        if (!recovery.TryGetProperty("BadSha256", out var badSha) || badSha.ValueKind != JsonValueKind.String) return false;
        if (!recovery.TryGetProperty("Outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String) return false;
        return true;
    }

    private static bool StringArrayOrNull(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String) return false;
        return true;
    }

    private static bool BindingsOrNull(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in value.EnumerateArray())
            if (!ValidBinding(item)) return false;
        return true;
    }

    private static bool RetainedOrNull(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in value.EnumerateArray())
            if (!ValidProviderRecord(item)) return false;
        return true;
    }

    private static readonly string[] ProviderStringFields =
    [
        "ConnectionId", "Provider", "AccountId", "RootNamespaceId", "HomeNamespaceId",
        "DisplayName", "Email", "AccountType", "CredentialReference"
    ];

    /// <summary>The predecessor's typed retained-provider record: string fields, a boolean active flag and a boolean
    /// capability profile (the typed record has no null capability profile).</summary>
    private static bool ValidProviderRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in ProviderStringFields)
            if (!StringOrNull(record, name)) return false;
        if (record.TryGetProperty("IsActive", out var active) &&
            active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        if (!record.TryGetProperty("Capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var member in capabilities.EnumerateObject())
            if (member.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        return true;
    }

    /// <summary>Projects the validated payload to ids: the explicit trimmed id, or the predecessor's derived account id.</summary>
    private static void ProjectBinding(JsonElement binding, List<string> ids)
    {
        ids.Add(BindingId(binding));
        if (!binding.TryGetProperty("AdditionalConnections", out var additional) ||
            additional.ValueKind != JsonValueKind.Array) return;
        foreach (var connection in additional.EnumerateArray()) ids.Add(BindingId(connection));
    }

    private static string BindingId(JsonElement binding)
    {
        if (binding.ValueKind != JsonValueKind.Object) throw InvalidList();
        if (binding.TryGetProperty("ConnectionId", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var explicitId = id.GetString()!.Trim();
            if (explicitId.Length > 0) return ValidatedConnectionId(explicitId);
        }
        if (binding.TryGetProperty("AccountId", out var account) && account.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(account.GetString()))
            return CreateConnectionId(account.GetString()!);
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
