using System.Globalization;

namespace Massdex.SyncShark;

// Disposable observation input, never effect authority. One bounded cache replaces
// a full object_version table scan for every file in a local inventory. A miss
// still hashes the held file; a conflicting retained identity still fails closed.
//
// This cache is NOT freshness authority. Its whole key is (volume, physical file identity, size,
// last-write), and a same-size edit that restores the timestamp keeps every part of that key, so an
// exact hit can still describe superseded bytes. No retained continuity attestation exists beside
// the standalone cache, therefore a complete, restarted or uncertain local inventory must hash the
// held bytes instead of consulting this cache. No production path loads it for that reason; only the
// complete continuous journal/watcher index in SyncLocalInventoryIndex may reuse an unchanged entry.
internal sealed class SyncLocalContentHashCache(uint volumeSerialNumber, int maximumEntries)
{
    internal const int DefaultMaximumEntries = 8192;
    private readonly record struct Key(string FileId, long Size, string LastWriteUtc);
    private readonly Dictionary<Key, string?> _hashes = new();
    internal int Count => _hashes.Count;

    internal void Add(string fileId, long size, string lastWriteUtc, string hash)
    {
        var key = new Key(fileId.ToLowerInvariant(), size, lastWriteUtc);
        if (_hashes.TryGetValue(key, out var retained))
        {
            if (!string.Equals(retained, hash, StringComparison.Ordinal)) _hashes[key] = null;
        }
        else if (_hashes.Count < maximumEntries) _hashes.Add(key, hash);
        // At the bound, keep checking ALL later rows for conflicts with retained
        // keys. Unknown keys remain misses, rather than becoming trusted hashes.
    }

    internal Task<string?> ResolveAsync(PilotLocalFileFingerprint fingerprint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (fingerprint.VolumeSerialNumber != volumeSerialNumber) return Task.FromResult<string?>(null);
        var key = new Key(fingerprint.FileId.ToString("x16", CultureInfo.InvariantCulture),
            fingerprint.Size, fingerprint.LastWriteUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (!_hashes.TryGetValue(key, out var hash)) return Task.FromResult<string?>(null);
        if (hash is null) throw new InvalidDataException(
            "One unchanged local physical identity has conflicting retained content hashes.");
        return Task.FromResult<string?>(hash);
    }
}

public sealed partial class SyncAuthorityDatabase
{
    // Reads retained object_version rows as a bounded cache of previously observed hashes. It is never
    // proof that the held bytes still match, so it must not be used to satisfy a complete, restarted or
    // uncertain inventory.
    internal async Task<SyncLocalContentHashCache> LoadLocalContentHashCacheAsync(uint volumeSerialNumber,
        string? requiredAlgorithm, CancellationToken cancellationToken = default,
        Func<int, int, Task>? onRead = null,
        int maximumEntries = SyncLocalContentHashCache.DefaultMaximumEntries)
    {
        if (maximumEntries is < 1 or > SyncLocalContentHashCache.DefaultMaximumEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var cache = new SyncLocalContentHashCache(volumeSerialNumber, maximumEntries);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_extract(identity_json,'$.fileId'),size_bytes,
                   json_extract(identity_json,'$.lastWriteUtc'),content_hash
            FROM object_version
            WHERE side='local' AND content_hash IS NOT NULL AND size_bytes IS NOT NULL
              AND lower(json_extract(identity_json,'$.volumeSerial'))=lower($volume)
              AND coalesce(json_extract(identity_json,'$.contentAlgorithm'),$legacyAlgorithm)=$algorithm;
            """;
        command.Parameters.AddWithValue("$volume", volumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$legacyAlgorithm", SyncContentIdentity.DropboxBlockSha256);
        command.Parameters.AddWithValue("$algorithm", requiredAlgorithm ?? SyncContentIdentity.DropboxBlockSha256);
        var read = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.IsDBNull(0) && !reader.IsDBNull(2))
                cache.Add(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3));
            read++;
            if (onRead is not null && read % 256 == 0) await onRead(read, cache.Count);
        }
        if (onRead is not null) await onRead(read, cache.Count);
        return cache;
    }
}
