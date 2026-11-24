namespace Massdex.SyncShark.Engine.Local;

/// <summary>A failed page never transfers any records or continuation to the consumer.</summary>
public sealed record NtfsUsnPage(NtfsUsnPageStatus Status, ulong ConsumedNextUsn, int? NativeError, IReadOnlyList<UsnRecord>? Records = null);
