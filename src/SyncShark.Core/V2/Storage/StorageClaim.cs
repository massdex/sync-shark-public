namespace Massdex.SyncShark.V2;

/// <summary>Outcome of one atomic claim; only <see cref="StorageClaim.None"/> means another worker owns the row.</summary>
internal enum StorageClaim { None = 0, Requested = 1, Normal = 2 }
