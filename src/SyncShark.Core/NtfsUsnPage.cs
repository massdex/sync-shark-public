namespace Massdex.SyncShark;

/// <summary>
/// One successful native USN journal page result: its classification, the native continuation
/// boundary actually consumed, and the native failure code where the platform supplied one.
/// </summary>
/// <param name="Status">The classified outcome of this single page read.</param>
/// <param name="ConsumedNextUsn">The continuation boundary reached without consuming a record that crosses it; it is not the requested finite end unless that end was reached in full.</param>
/// <param name="NativeError">The native failure code when the platform supplied one; otherwise <see langword="null"/>.</param>
public sealed record NtfsUsnPage(
    NtfsUsnPageStatus Status,
    ulong ConsumedNextUsn,
    int? NativeError);
