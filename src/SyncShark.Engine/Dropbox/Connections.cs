using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox connection list at <c>&lt;ProductRoot&gt;/state/dropbox-connection.jsonl</c>, as in 0.3.95.
/// It holds only connection ids; the account's saved root identity lives with each pair, so no credential reference
/// is stored or returned here. Migration resolves 0.3.95's authoritative value: its alternating
/// <c>dropbox-connection-a.json</c>/<c>-b.json</c> slots when either exists, otherwise the legacy append log.</summary>
public sealed class Connections
{
    private const string StateFolder = "state";
    private const string FileName = "dropbox-connection.jsonl";
    private const string SlotA = "dropbox-connection-a.json";
    private const string SlotB = "dropbox-connection-b.json";
    private const int SlotSchemaVersion = 1;
    private const int MaximumSlotBytes = 1024 * 1024;
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

    /// <summary>Reads the product's present connection ids; a missing list is empty and a malformed list is refused.</summary>
    public IReadOnlyList<string> Read(string productRoot) => ReadFile(FilePath(productRoot));

    /// <summary>Verify the source and target lists and the required connections before the store switch. Resolve the
    /// source's authoritative 0.3.95 value (successor slots first, else its append log) and publish it create-only into
    /// the target's list when the target has none; accept an existing target only when its connection set is equal.
    /// On refusal remove only this attempt's files and throw before database publication; on success return cleanup
    /// for a failed switch, or null when nothing was created.</summary>
    public Action? Verify(string sourceRoot, string productRoot, IReadOnlyList<string> requiredConnectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot, nameof(sourceRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(requiredConnectionIds, nameof(requiredConnectionIds));
        var source = FilePath(sourceRoot);
        var target = FilePath(productRoot);
        var sourceSet = AuthoritativeSource(sourceRoot);
        var sourcePresent = File.Exists(source) || File.Exists(Path.Combine(sourceRoot, StateFolder, SlotA)) ||
                            File.Exists(Path.Combine(sourceRoot, StateFolder, SlotB));
        var created = false;
        try
        {
            if (File.Exists(target))
            {
                if (!ReadFile(target).SequenceEqual(sourceSet, StringComparer.Ordinal))
                    throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            }
            else if (sourcePresent)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var stage = StagePath(target);
                if (!WriteStage(stage, output =>
                    {
                        foreach (var id in sourceSet)
                        {
                            output.Write(JsonSerializer.SerializeToUtf8Bytes(id));
                            output.WriteByte((byte)'\n');
                        }
                    }))
                    throw new IOException();
                try { File.Move(stage, target); }
                catch { TryDelete(stage); throw; }
                created = true;
            }
            var actual = ReadFile(target);
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

    /// <summary>Replaces the list atomically with one JSON line per distinct connection id.</summary>
    public void Save(string productRoot, IReadOnlyList<string> connections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(connections, nameof(connections));
        var ids = connections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var id in ids) _ = CredentialStore.AccountTarget(id);
        var target = FilePath(productRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stage = StagePath(target);
        if (!WriteStage(stage, output =>
            {
                foreach (var id in ids)
                {
                    output.Write(JsonSerializer.SerializeToUtf8Bytes(id));
                    output.WriteByte((byte)'\n');
                }
            }))
            throw new IOException();
        try { File.Move(stage, target, overwrite: true); }
        catch { TryDelete(stage); throw; }
    }

    private static string FilePath(string productRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        return Path.Combine(productRoot, StateFolder, FileName);
    }

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

    // --- 0.3.95 authoritative value: successor slots first, then the legacy append log -------------------------

    private static IReadOnlyList<string> AuthoritativeSource(string productRoot)
    {
        var state = Path.Combine(productRoot, StateFolder);
        var a = ReadSlot(Path.Combine(state, SlotA));
        var b = ReadSlot(Path.Combine(state, SlotB));
        if (a.Exists || b.Exists)
        {
            // A successor exists, so the legacy log is never consulted; both invalid refuses rather than roll back.
            var selected = SelectSlot(a, b) ?? throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            var ids = new List<string>();
            if (selected.Present) AddBinding(selected.Value, ids);
            return SortedDistinct(ids);
        }
        return ReadFile(Path.Combine(state, FileName));
    }

    private sealed record Slot(bool Exists, bool Valid, long Generation, bool Present, JsonElement Value);

    private static Slot ReadSlot(string path)
    {
        if (!File.Exists(path)) return new Slot(false, false, 0, false, default);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > MaximumSlotBytes) return new Slot(true, false, 0, false, default);
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

    // --- The list itself: our canonical JSON-string lines, or the 0.3.95 append log ----------------------------

    private static IReadOnlyList<string> ReadFile(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        var bytes = File.ReadAllBytes(path);
        var lines = TerminatedLines(bytes);
        var ids = new List<string>();
        var isCanonical = -1;
        var remainder = bytes.Length > 0 && bytes[^1] != (byte)'\n';
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                // The predecessor's append scan stops at a bad record and keeps its valid prefix; our own
                // canonical list is never partially written, so a bad line there refuses.
                if (isCanonical == 0) break;
                throw InvalidList();
            }
            using (document)
            {
                var root = document.RootElement;
                var stringLine = root.ValueKind == JsonValueKind.String;
                if (isCanonical < 0) isCanonical = stringLine ? 1 : 0;
                else if ((isCanonical == 1) != stringLine) throw InvalidList();
                if (stringLine) ids.Add(ValidatedConnectionId(root.GetString()!));
                else if (!TryAddLegacyRecord(root, ids)) break;
            }
        }
        // The predecessor's append scan only reads terminated records; our own canonical list is always terminated.
        if (isCanonical == 1 && remainder) throw InvalidList();
        return SortedDistinct(ids);
    }

    /// <summary>Only newline-terminated lines are records (the 0.3.95 append scan); a partial tail is not a record.</summary>
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

    /// <summary>Applies one 0.3.95 append record after the predecessor's schema, operation and payload-hash checks;
    /// the current value is the last applied set (or none after an applied delete). False stops the scan, keeping the
    /// valid prefix exactly as the predecessor did.</summary>
    private static bool TryAddLegacyRecord(JsonElement record, List<string> ids)
    {
        if (record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            schema.GetInt32() != SlotSchemaVersion ||
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
            if (!record.TryGetProperty("Recovery", out var recovery) || recovery.ValueKind != JsonValueKind.Object) return false;
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

    private static void AddBinding(JsonElement binding, List<string> ids)
    {
        if (binding.ValueKind != JsonValueKind.Object ||
            !binding.TryGetProperty("ConnectionId", out var primary) || primary.ValueKind != JsonValueKind.String)
            throw InvalidList();
        ids.Add(ValidatedConnectionId(primary.GetString()!));
        if (!binding.TryGetProperty("AdditionalConnections", out var additional) ||
            additional.ValueKind != JsonValueKind.Array) return;
        foreach (var connection in additional.EnumerateArray())
        {
            if (connection.ValueKind != JsonValueKind.Object ||
                !connection.TryGetProperty("ConnectionId", out var id) || id.ValueKind != JsonValueKind.String)
                throw InvalidList();
            ids.Add(ValidatedConnectionId(id.GetString()!));
        }
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
