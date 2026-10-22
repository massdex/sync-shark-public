namespace Massdex.SyncShark;

internal sealed class SyncObjectVersionIdentityConflictException(string message)
    : InvalidOperationException(message)
{
}

public sealed partial class SyncAuthorityDatabase
{
    // A unique physical version may already have a different canonical row ID.
    // Return that retained ID; never let INSERT OR IGNORE manufacture a reference.
    internal async Task<string> RegisterObjectVersionExactAsync(string objectVersionId,
        string objectId, string side, string identityJson, string? contentHash, long? size,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(objectVersionId, nameof(objectVersionId));
        RequireIdentifier(objectId, nameof(objectId));
        RequireJson(identityJson, nameof(identityJson));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (contentHash is not null) RequireHash(contentHash, nameof(contentHash));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        string? retainedId = null;
        await WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT object_version_id,object_id,side,identity_json,content_hash,size_bytes
                FROM object_version WHERE object_version_id=$id
                    OR (object_id=$object AND side=$side AND identity_json=$identity);
                """;
            command.Parameters.AddWithValue("$id", objectVersionId);
            command.Parameters.AddWithValue("$object", objectId);
            command.Parameters.AddWithValue("$side", side);
            command.Parameters.AddWithValue("$identity", identityJson);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var sameIdentity = reader.GetString(1) == objectId && reader.GetString(2) == side &&
                        reader.GetString(3) == identityJson;
                    var retainedHash = reader.IsDBNull(4) ? null : reader.GetString(4);
                    long? retainedSize = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                    if (!sameIdentity || retainedSize != size ||
                        !string.Equals(retainedHash, contentHash, StringComparison.OrdinalIgnoreCase))
                        throw new SyncObjectVersionIdentityConflictException(
                            "The immutable object-version identity is already bound to different content or authority.");
                    retainedId = reader.GetString(0);
                }
            }
            if (retainedId is not null) return;
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO object_version(object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc)
                VALUES($id,$object,$side,$identity,$hash,$size,$utc);
                """, cancellationToken, ("$id", objectVersionId), ("$object", objectId),
                ("$side", side), ("$identity", identityJson), ("$hash", contentHash),
                ("$size", size), ("$utc", Utc(DateTimeOffset.UtcNow)));
            retainedId = objectVersionId;
        }, cancellationToken);
        return retainedId ?? throw new InvalidOperationException("No object version was retained.");
    }
}
