namespace Massdex.SyncShark.V2;

/// <summary>
/// Reads the exact immutable bytes one durable Recovery row names, without restoring, uploading or deleting anything.
/// </summary>
/// <remarks>
/// A reader is bound to one endpoint root. It refuses a row that belongs to another pair (when the writer knows its
/// pair), the other side or another provider before any provider request. Describing reads bounded metadata without
/// downloading or hashing retained bytes. A confirmed local writer may repair its authenticated interrupted archive
/// access envelope first; dry-run facades use the mutation-free preview helpers instead. Opening describes again
/// and then uses the endpoint's existing verified read path, which hashes the bytes,
/// so a stale or missing retained version fails with an explanatory exception while every byte of user data stays put.
/// <para>
/// Exception classification for callers: <see cref="FileNotFoundException"/> and <see cref="InvalidDataException"/>
/// mean the retained version is gone, the row does not match it, or this writer does not own it. <see cref="IOException"/>
/// means the provider or Recovery location could not be read right now and the operation should be retried.
/// </para>
/// </remarks>
internal interface IRecoveryRecordReader
{
    /// <summary>
    /// The exact retained file the Recovery row names, at <see cref="RecoveryRecord.Path"/>, carrying the recorded
    /// hash, size and provider revision. No byte read, download or content write. Confirmed local writers may repair
    /// an authenticated archive access envelope; preview facades never repair it. Throws when ownership or availability fails.
    /// </summary>
    Task<ItemState> DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct);

    /// <summary>
    /// Opens the exact retained bytes for an actual apply after describing the row and verifying them through the
    /// provider's existing immutable-read path. The caller owns and disposes the returned stream.
    /// </summary>
    Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct);

    /// <summary>
    /// Opens the exact retained bytes using metadata already described for this row in the same operation, so an
    /// apply does not request the provider's historical metadata twice. The implementation must re-validate the
    /// supplied state against the durable row and the provider's own identity rules before opening, and must not
    /// cache it; the default keeps every provider or fixture without a narrower path correct by describing again.
    /// The caller owns and disposes the returned stream.
    /// </summary>
    Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord recovery, ItemState described, CancellationToken ct) =>
        OpenRecoveryRecordAsync(recovery, ct);
}
