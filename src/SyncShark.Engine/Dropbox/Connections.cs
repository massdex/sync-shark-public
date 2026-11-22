using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox connection list at <c>&lt;ProductRoot&gt;/state/dropbox-connection.jsonl</c>, as in 0.3.95.
/// It holds only connection ids; the account's saved root identity lives with each pair, so no credential reference
/// is stored or returned here.</summary>
public sealed class Connections
{
    private const string StateFolder = "state";
    private const string FileName = "dropbox-connection.jsonl";
    private const string ConnectionPrefix = "connection-";
    private const int ConnectionHashLength = 24;
    private const string StageFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string SetOperation = "set";
    private const string DeleteOperation = "delete";

    /// <summary>The stable connection id of a Dropbox account: <c>connection-</c> and 24 lowercase hex digits of its
    /// domain-separated account hash, as 0.3.95 created it.</summary>
    public static string CreateConnectionId(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId, nameof(accountId));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("dropbox\n" + accountId));
        return ConnectionPrefix + Convert.ToHexString(hash).ToLowerInvariant()[..ConnectionHashLength];
    }

    /// <summary>Reads the present connection ids; a missing list is empty and a malformed list is refused.</summary>
    public IReadOnlyList<string> Read(string productRoot) => ReadFile(FilePath(productRoot));

    /// <summary>Verify the source and target lists and the required connections before the store switch. Copy the
    /// source's list create-only when the target has none; accept an existing target only when it is byte-identical.
    /// On refusal remove only this attempt's files and throw before database publication; on success return cleanup
    /// for a failed switch, or null when nothing was created.</summary>
    public Action? Verify(string sourceRoot, string productRoot, IReadOnlyList<string> requiredConnectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot, nameof(sourceRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        ArgumentNullException.ThrowIfNull(requiredConnectionIds, nameof(requiredConnectionIds));
        var source = FilePath(sourceRoot);
        var target = FilePath(productRoot);
        string? stage = null;
        var created = false;
        try
        {
            if (File.Exists(target))
            {
                if (File.Exists(source) && !File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target)))
                    throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            }
            else if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                stage = StagePath(target);
                CopyCreateOnly(source, stage);
                File.Move(stage, target);
                stage = null;
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
            if (stage is not null) TryDelete(stage);
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
        try
        {
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var id in ids)
                {
                    output.Write(JsonSerializer.SerializeToUtf8Bytes(id));
                    output.WriteByte((byte)'\n');
                }
                output.Flush(true);
            }
            File.Move(stage, target, overwrite: true);
            stage = null;
        }
        finally
        {
            if (stage is not null) TryDelete(stage);
        }
    }

    private static string FilePath(string productRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot, nameof(productRoot));
        return Path.Combine(productRoot, StateFolder, FileName);
    }

    private static string StagePath(string target) =>
        target + "." + DateTime.UtcNow.ToString(StageFormat, CultureInfo.InvariantCulture) + ".new";

    private static void CopyCreateOnly(string source, string stage)
    {
        var bytes = File.ReadAllBytes(source);
        using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }

    private static IReadOnlyList<string> ReadFile(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        var ids = new List<string>();
        foreach (var raw in File.ReadAllText(path).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            using var document = ParseLine(line);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                ids.Add(CredentialStore.AccountTarget(root.GetString()!));
                continue;
            }
            if (root.ValueKind != JsonValueKind.Object) throw InvalidList();
            if (root.TryGetProperty("Operation", out var operation) && operation.ValueKind == JsonValueKind.String)
            {
                // 0.3.95 appended one record per save; the current list is the last set (or none after a delete).
                switch (operation.GetString())
                {
                    case SetOperation:
                        ids.Clear();
                        if (!root.TryGetProperty("Value", out var value)) throw InvalidList();
                        AddBinding(value, ids);
                        break;
                    case DeleteOperation:
                        ids.Clear();
                        break;
                }
                continue;
            }
            AddBinding(root, ids);
        }
        return ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddBinding(JsonElement binding, List<string> ids)
    {
        if (binding.ValueKind == JsonValueKind.String)
        {
            ids.Add(CredentialStore.AccountTarget(binding.GetString()!));
            return;
        }
        if (binding.ValueKind != JsonValueKind.Object ||
            !binding.TryGetProperty("ConnectionId", out var primary) || primary.ValueKind != JsonValueKind.String)
            throw InvalidList();
        ids.Add(CredentialStore.AccountTarget(primary.GetString()!));
        if (!binding.TryGetProperty("AdditionalConnections", out var additional) ||
            additional.ValueKind != JsonValueKind.Array) return;
        foreach (var connection in additional.EnumerateArray())
        {
            if (connection.ValueKind != JsonValueKind.Object ||
                !connection.TryGetProperty("ConnectionId", out var id) || id.ValueKind != JsonValueKind.String)
                throw InvalidList();
            ids.Add(CredentialStore.AccountTarget(id.GetString()!));
        }
    }

    private static InvalidDataException InvalidList() => new(Text.Get(TextKey.RootDropboxIdentityInvalid));

    private static JsonDocument ParseLine(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { throw InvalidList(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
