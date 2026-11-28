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
    private readonly object membership = new();
    private const string StateFolder = "state";
    private const string FileName = "dropbox-connection.jsonl";
    private const string SlotA = "dropbox-connection-a.json";
    private const string SlotB = "dropbox-connection-b.json";
    private const int SlotSchemaVersion = 1;
    // Released format: 08a9d06b2, AppendOnlyJsonStore.cs:10-11 and DualSlotJsonStore.cs:9.
    private const int LegacyMaximumBytes = 1024 * 1024;
    private const string ConnectionPrefix = "connection-";
    private const int ConnectionHashLength = 24;

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
    public IReadOnlyList<string> Read(string productRoot) { lock (membership) return Resolve(productRoot, out _).Ids; }

    /// <summary>Verify the source's authoritative 0.3.95 value and the target before the store switch. In place, the
    /// root's own JSONL is evidence and is never overwritten, and the value Read will expose must equal the authoritative
    /// source set (refusing an unresolved divergence). For distinct roots the target is created only when absent (copying
    /// the authoritative append log's bytes when that log supplies them); an existing target must carry the same set and,
    /// when the source is file-backed, the same bytes except for append-to-canonical below. A slot-only source has no
    /// authoritative append bytes, so only its set is compared (the slot is authoritative). A same-set canonical target
    /// with different bytes is accepted and neither side is overwritten (R47); the migration owner reports it once via
    /// the existing notice path. On refusal remove only this attempt's files and throw before database publication; on
    /// success return cleanup for a failed switch, or null when nothing was created.</summary>
    public Action? Verify(string sourceRoot, string productRoot, IReadOnlyList<string> requiredConnectionIds) =>
        Verify(sourceRoot, productRoot, requiredConnectionIds, null);

    public Action? Verify(string sourceRoot, string productRoot, IReadOnlyList<string> requiredConnectionIds,
        Action<string>? differingAppendLog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot, nameof(sourceRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(requiredConnectionIds, nameof(requiredConnectionIds));
        var source = FilePath(sourceRoot);
        var target = FilePath(productRoot);
        var sameRoot = SameRoot(sourceRoot, productRoot);
        var differing = false;
        var created = false;
        try
        {
            VerifyTarget(sourceRoot, productRoot, sameRoot, requiredConnectionIds, ref created, ref differing);
            if (differing) differingAppendLog?.Invoke(Path.GetFullPath(source));
            return created ? () => TryDelete(target) : null;
        }
        catch
        {
            if (created) TryDelete(target);
            throw;
        }
    }

    private static void VerifyTarget(string sourceRoot, string productRoot, bool sameRoot,
        IReadOnlyList<string> required, ref bool created, ref bool differing)
    {
        IReadOnlyList<string> actual;
        if (sameRoot)
        {
            var resolved = Resolve(productRoot, out _);
            if (resolved.Form == ConnectionListFormat.ListShape.Canonical)
            {
                // With no slot the canonical list is itself the authority (a store upgrade in place on a machine that never had slots).
                var authoritative = SlotsExist(sourceRoot) ? SlotSet(sourceRoot) : resolved.Ids;
                if (!resolved.Ids.SequenceEqual(authoritative, StringComparer.Ordinal)) throw ConnectionListFormat.InvalidList();
            }
            actual = resolved.Ids;
        }
        else actual = VerifyDistinctTarget(sourceRoot, productRoot, ref created, ref differing);
        foreach (var id in required)
            if (!actual.Contains(id, StringComparer.Ordinal))
                throw new InvalidDataException(Text.Get(TextKey.AccountMissing, (TextArg.Account, id)));
    }

    private static IReadOnlyList<string> VerifyDistinctTarget(string sourceRoot, string productRoot,
        ref bool created, ref bool differing)
    {
        IReadOnlyList<string> actual;
        // A slot supersedes the old log, even if that log is unreadable or looks canonical.
        var slotsAuthoritative = SlotsExist(sourceRoot);
        byte[]? bytes = null;
        var form = slotsAuthoritative ? ConnectionListFormat.ListShape.Absent : ReadList(FilePath(sourceRoot), out bytes);
        var sourceSet = slotsAuthoritative ? SlotSet(sourceRoot) : DecodeList(form, bytes);
        var resolved = Resolve(productRoot, out var targetBytes);
        var targetPresent = resolved.Form != ConnectionListFormat.ListShape.Absent || SlotsExist(productRoot);
        if (targetPresent)
        {
            if (!resolved.Ids.SequenceEqual(sourceSet, StringComparer.Ordinal))
                throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            differing = form == ConnectionListFormat.ListShape.Append && resolved.Form == ConnectionListFormat.ListShape.Canonical;
            if (!differing && bytes is not null && (targetBytes is null || !bytes.AsSpan().SequenceEqual(targetBytes)))
                throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            actual = resolved.Ids;
        }
        else if (slotsAuthoritative || bytes is not null)
        {
            var published = bytes is not null
                ? Publish(FilePath(productRoot), output => output.Write(bytes))
                : Publish(FilePath(productRoot), output => WriteIds(output, sourceSet));
            if (!published) throw new IOException();
            created = true;
            actual = Resolve(productRoot, out _).Ids;
            if (!actual.SequenceEqual(sourceSet, StringComparer.Ordinal)) throw ConnectionListFormat.InvalidList();
        }
        else actual = sourceSet;
        return actual;
    }

    /// <summary>Publish one membership change from the current authority; only a successful save switches reads to the fixed root.</summary>
    public void SetConnected(string productRoot, ref string readRoot, string connectionId, bool connected)
    {
        _ = CredentialStore.AccountTarget(connectionId);
        lock (membership)
        {
            var ids = Read(readRoot);
            Save(productRoot, (connected ? ids.Append(connectionId) : ids.Where(id => id != connectionId)).ToArray());
            Volatile.Write(ref readRoot, productRoot);
        }
    }

    /// <summary>Replaces the product's list atomically with its one canonical array line.</summary>
    public void Save(string productRoot, IReadOnlyList<string> connections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(connections, nameof(connections));
        var ids = connections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var id in ids) _ = CredentialStore.AccountTarget(id);
        lock (membership)
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

    private static ConnectionListFormat.ListShape ReadList(string path, out byte[]? bytes)
    {
        bytes = new FileInfo(path).Exists ? File.ReadAllBytes(path) : null;
        if (bytes is null) return ConnectionListFormat.ListShape.Absent;
        var form = ConnectionListFormat.ListForm(bytes);
        if (form == ConnectionListFormat.ListShape.Append && bytes.Length > LegacyMaximumBytes) bytes = null;
        return form;
    }

    private static (ConnectionListFormat.ListShape Form, IReadOnlyList<string> Ids) Resolve(string productRoot, out byte[]? bytes)
    {
        var form = ReadList(FilePath(productRoot), out bytes);
        return (form, form != ConnectionListFormat.ListShape.Canonical && SlotsExist(productRoot) ? SlotSet(productRoot) : DecodeList(form, bytes));
    }

    private static IReadOnlyList<string> DecodeList(ConnectionListFormat.ListShape form, byte[]? bytes) => form switch
    {
        ConnectionListFormat.ListShape.Absent => Array.Empty<string>(),
        ConnectionListFormat.ListShape.Canonical => ConnectionListFormat.CanonicalIds(bytes!),
        _ => ConnectionListFormat.AppendSet(bytes ?? throw ConnectionListFormat.InvalidList())
    };

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
        return selected.Present && selected.Value is not null ? ConnectionListFormat.ProjectBinding(selected.Value) : Array.Empty<string>();
    }

    private sealed record Slot(bool Valid, long Generation, bool Present, ConnectionListFormat.DropboxConnectionBinding? Value);

    private static Slot ReadSlot(string path)
    {
        var invalid = new Slot(false, 0, false, null);
        if (!File.Exists(path)) return invalid;
        if (new FileInfo(path).Length is 0 or > LegacyMaximumBytes) return invalid;
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("Value", out var rawValue) || rawValue.ValueKind == JsonValueKind.Null)
                return invalid;
            var record = JsonSerializer.Deserialize<ConnectionListFormat.SlotRecord<ConnectionListFormat.MutableJsonValue<ConnectionListFormat.DropboxConnectionBinding>>>(bytes, ConnectionListFormat.JsonOptions);
            if (record is null || record.SchemaVersion != SlotSchemaVersion || record.Generation <= 0 ||
                record.Value is null || record.PayloadSha256 is null)
                return invalid;
            if (!ConnectionListFormat.HashMatches(record.PayloadSha256, Encoding.UTF8.GetBytes(rawValue.GetRawText()))) return invalid;
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
