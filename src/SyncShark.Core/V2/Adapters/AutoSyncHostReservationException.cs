namespace Massdex.SyncShark.V2;

/// <summary>Address reservation failed before any store authority or engine was acquired.</summary>
public sealed class AutoSyncHostReservationException(Exception inner)
    : IOException("The AutoSync pipe could not be reserved; check for an existing host.", inner);
