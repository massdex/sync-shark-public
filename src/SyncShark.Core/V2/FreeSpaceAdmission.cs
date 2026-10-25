namespace Massdex.SyncShark.V2;

/// <summary>The verdict: a reservation to release when the effect ends, or the pending refusal.</summary>
public sealed record FreeSpaceAdmission(IDisposable? Reservation, FreeSpaceRefusal? Refusal)
{
    public static FreeSpaceAdmission Granted { get; } = new(null, null);
}
