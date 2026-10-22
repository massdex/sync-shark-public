namespace Massdex.SyncShark;

// AUD04: fenced operation-lease renewal. This partial adds the one conditional renewal transition that lets a
// legitimate long-running effect keep its exact operation lease. Renewal is never a takeover: it extends only
// the lease row this worker already holds, and only while every fence component still matches, so an expired,
// released, stolen or foreign lease can never be revived or extended by it.
public sealed partial class SyncAuthorityDatabase
{
    /// <summary>
    /// Conditionally extends the exact active operation lease held by this worker, keeping the same lease
    /// identity, operation, worker and fencing token. The extension happens only when the retained lease row
    /// is still active and unexpired against the database clock, still carries this worker's exact fencing
    /// token, and is still the operation's own current lease and fencing authority. Any mismatch returns
    /// <see langword="null"/> and writes nothing, so an expired, released, superseded or stolen lease stays
    /// terminal and is never revived.
    /// </summary>
    public async Task<SyncAuthorityLease?> TryRenewOperationLeaseAsync(string operationId, string workerId,
        SyncAuthorityLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        RequireIdentifier(operationId, nameof(operationId));
        RequireIdentifier(workerId, nameof(workerId));
        if (!string.Equals(operationId, lease.OperationId, StringComparison.Ordinal) ||
            !string.Equals(workerId, lease.WorkerId, StringComparison.Ordinal))
            return null;
        // Renewal preserves the exact acquired duration of this lease; it never lengthens or shortens it.
        var duration = lease.ExpiresUtc - lease.AcquiredUtc;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30)) return null;

        SyncAuthorityLease? renewed = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var now = DateTimeOffset.UtcNow;
            var expires = now.Add(duration);
            // The fence is re-verified immediately before the extension, inside the same serialized write
            // transaction, so an expired, released, superseded or stolen lease can never be extended.
            var current = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM lease l JOIN operation o ON o.operation_id=l.scope_id
                WHERE l.lease_id=$lease AND l.scope_kind='operation' AND l.scope_id=$operation
                  AND l.worker_id=$worker AND l.fencing_token=$token AND l.status='active'
                  AND julianday(l.expires_utc)>julianday('now')
                  AND o.current_lease_id=l.lease_id AND o.current_fencing_token=l.fencing_token;
                """, cancellationToken, ("$lease", lease.LeaseId), ("$operation", operationId),
                ("$worker", workerId), ("$token", lease.FencingToken));
            if (current != 1) return;
            await ExecuteAsync(connection, transaction,
                "UPDATE lease SET expires_utc=$expires WHERE lease_id=$lease AND status='active';",
                cancellationToken, ("$expires", Utc(expires)), ("$lease", lease.LeaseId));
            renewed = new SyncAuthorityLease(lease.LeaseId, operationId, workerId, lease.FencingToken,
                lease.AcquiredUtc, expires);
        }, cancellationToken);
        return renewed;
    }
}
