using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox connection list at <c>&lt;ProductRoot&gt;/state/dropbox-connection.jsonl</c>, as in 0.3.95.
/// It holds only connection ids; the account's saved root identity lives with each pair, so no credential reference
/// is stored or returned here. Migration resolves 0.3.95's authoritative value (its alternating
/// <c>dropbox-connection-a.json</c>/<c>-b.json</c> slots first, otherwise the validated legacy append log) and, on an
/// in-place upgrade, treats the root's own JSONL as source evidence rather than a competing target.</summary>
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
    private const string StageFormat = "yyyyMMdd'T'HHmmss'Z'";
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

    /// <summary>The product's present connection ids: our canonical lines when present, otherwise the 0.3.95 authoritative
    /// value of this root (slots first, then the validated append log). A missing list is empty; a malformed one refuses.</summary>
    public IReadOnlyList<string> Read(string productRoot) => ProductList(productRoot);

    /// <summary>Verify the source's authoritative 0.3.95 value and the target before the store switch. In place, the
    /// root's own JSONL is source evidence: it is never overwritten, and only a slot-only root gets a create-only list.
    /// For distinct roots the target is created only when absent; an existing distinct append target must be byte-identical
    /// to the source log (BC8) and an existing canonical target must carry the same set. On refusal remove only this
    /// attempt's files and throw before database publication; on success return cleanup for a failed switch, or null
    /// when nothing was created.</summary>
    public Action? Verify(string sourceRoot, string productRoot, IReadOnlyList<string> requiredConnectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot, nameof(sourceRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(requiredConnectionIds, nameof(requiredConnectionIds));
        var source = FilePath(sourceRoot);
        var target = FilePath(productRoot);
        var sourceSet = SourceSet(sourceRoot);
        var sameRoot = SameRoot(sourceRoot, productRoot);
        var created = false;
        try
        {
            if (sameRoot)
            {
                // The predecessor's own JSONL is evidence, not a competing target; publish only when no list exists.
                if (!File.Exists(target) && SlotsExist(sourceRoot))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var stage = StagePath(target);
                    if (!WriteStage(stage, output => WriteIds(output, sourceSet))) throw new IOException();
                    try { File.Move(stage, target); }
                    catch { TryDelete(stage); throw; }
                    created = true;
                }
            }
            else if (File.Exists(target))
            {
                if (ListForm(target) == ListShape.Canonical)
                {
                    if (!ProductList(productRoot).SequenceEqual(sourceSet, StringComparer.Ordinal))
                        throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
                }
                else if (!File.Exists(source) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target)))
                {
                    // BC8 step 5: an existing distinct append list is accepted only byte-identically.
                    throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
                }
            }
            else if (sourceSet.Count > 0 || SlotsExist(sourceRoot) || File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var stage = StagePath(target);
                if (!WriteStage(stage, output => WriteIds(output, sourceSet))) throw new IOException();
                try { File.Move(stage, target); }
                catch { TryDelete(stage); throw; }
                created = true;
            }
            var actual = created || !sameRoot ? ProductList(productRoot) : sourceSet;
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

    /// <summary>Replaces the product's list atomically with one JSON line per distinct connection id.</summary>
    public void Save(string productRoot, IReadOnlyList<string> connections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(connections, nameof(connections));
        var ids = connections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var id in ids) _ = CredentialStore.AccountTarget(id);
        var target = FilePath(productRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stage = StagePath(target);
        if (!WriteStage(stage, output => WriteIds(output, ids))) throw new IOException();
        try { File.Move(stage, target, overwrite: true); }
        catch { TryDelete(stage); throw; }
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
        target + "." + DateTime.UtcNow.ToString(StageFormat, CultureInfo.InvariantCulture) + ".new";

    /// <summary>Creates and flushes this attempt's own stage; an existing name is never touched, and a failed write
    /// removes only the stage this attempt created.</summary>
    private static bool WriteStage(string stage, Action<FileStream> write)
    {
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
        foreach (var id in ids)
        {
            output.Write(JsonSerializer.SerializeToUtf8Bytes(id));
            output.WriteByte((byte)'\n');
        }
    }

    // --- The product's list: our canonical JSON-string lines, or the 0.3.95 value of a root not yet upgraded --------

    private enum ListShape { Absent, Canonical, Append }

    private static IReadOnlyList<string> ProductList(string productRoot)
    {
        var path = FilePath(productRoot);
        return ListForm(path) switch
        {
            ListShape.Canonical => CanonicalIds(path),
            _ => SlotsExist(productRoot) ? SlotSet(productRoot) : AppendSet(path)
        };
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
        if (selected.Present) AddBinding(selected.Value, ids);
        return SortedDistinct(ids);
    }

    private sealed record Slot(bool Exists, bool Valid, long Generation, bool Present, JsonElement Value);

    private static Slot ReadSlot(string path)
    {
        if (!File.Exists(path)) return new Slot(false, false, 0, false, default);
        if (new FileInfo(path).Length is 0 or > MaximumStateBytes) return new Slot(true, false, 0, false, default);
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
                return new Slot(true, false, 0, false, default);
            if (!present.GetBoolean()) return new Slot(true, true, generation, false, default);
            return value.TryGetProperty("Value", out var binding) && binding.ValueKind == JsonValueKind.Object
                ? new Slot(true, true, generation, true, binding.Clone())
                : new Slot(true, false, 0, false, default);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return new Slot(true, false, 0, false, default);
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
        if (new FileInfo(path).Length is 0 or > MaximumStateBytes) return ListShape.Append;
        var bytes = File.ReadAllBytes(path);
        foreach (var line in TerminatedLines(bytes))
        {
            if (line.Length == 0) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.ValueKind == JsonValueKind.String ? ListShape.Canonical : ListShape.Append;
            }
            catch (JsonException) { return ListShape.Append; }
        }
        // An existing file with no non-empty terminated line is our empty canonical list.
        return ListShape.Canonical;
    }

    /// <summary>Our canonical list: every terminated line is a JSON string id; a partial tail refuses.</summary>
    private static IReadOnlyList<string> CanonicalIds(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var ids = new List<string>();
        foreach (var line in TerminatedLines(bytes))
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { throw InvalidList(); }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.String) throw InvalidList();
                ids.Add(ValidatedConnectionId(document.RootElement.GetString()!));
            }
        }
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n') throw InvalidList();
        return SortedDistinct(ids);
    }

    /// <summary>The 0.3.95 append log: newline-terminated records only, the predecessor's schema/operation/payload-hash
    /// checks, the valid-prefix stop rule, its record ceiling, and the normalized/derived primary id.</summary>
    private static IReadOnlyList<string> AppendSet(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        if (new FileInfo(path).Length > MaximumStateBytes) throw InvalidList();
        var bytes = File.ReadAllBytes(path);
        var ids = new List<string>();
        var records = 0;
        foreach (var line in TerminatedLines(bytes))
        {
            if (line.Length == 0) continue;
            if (++records > MaximumAppendRecords) throw InvalidList();
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { break; } // the predecessor stops with its valid prefix (a first bad line included)
            using (document)
            {
                if (!TryAddLegacyRecord(document.RootElement, ids)) break;
            }
        }
        return SortedDistinct(ids);
    }

    private static List<byte[]> TerminatedLines(byte[] bytes)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            var length = index - start;
            if (length > 0 && bytes[index - 1] == (byte)'\r') length--;
            lines.Add(bytes.AsSpan(start, length).ToArray());
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
                // The predecessor requires a recovery record's named evidence file.
                if (!record.TryGetProperty("Recovery", out var recovery) || recovery.ValueKind != JsonValueKind.Object ||
                    !recovery.TryGetProperty("EvidenceFile", out var evidence) || evidence.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(evidence.GetString()))
                    return false;
                payload = recovery;
            }
            else return false;
            if (!CryptographicOperations.FixedTimeEquals(claimed, SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText()))))
                return false;
            if (kind == RecoveryOperation) return true;
            ids.Clear();
            AddBinding(payload, ids);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A 0.3.95 binding's connection id: its own id, or the predecessor's derived id from a non-blank account id.</summary>
    private static void AddBinding(JsonElement binding, List<string> ids)
    {
        if (binding.ValueKind != JsonValueKind.Object) throw InvalidList();
        ids.Add(BindingId(binding));
        if (!binding.TryGetProperty("AdditionalConnections", out var additional) ||
            additional.ValueKind != JsonValueKind.Array) return;
        foreach (var connection in additional.EnumerateArray()) ids.Add(BindingId(connection));
    }

    private static string BindingId(JsonElement binding)
    {
        if (binding.ValueKind != JsonValueKind.Object) throw InvalidList();
        if (binding.TryGetProperty("ConnectionId", out var id) && id.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(id.GetString()))
            return ValidatedConnectionId(id.GetString()!);
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
