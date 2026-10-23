namespace Massdex.SyncShark.V2;

/// <summary>
/// The one continuous-sync monitor wording (edge-messages.json): the state text the window's tray and pair rows show
/// and the projection the monitor query returns. The window reads <see cref="StateText"/> for a changed pair.
/// </summary>
public static class MonitorMapping
{
    /// <summary>The projection of one pair's current continuous-sync state; pending-row count is not tracked.</summary>
    public static SyncAuthorityMonitorProjection ToProjection(PairAutoState state, DateTimeOffset now) =>
        new(state.PairId, EdgeMessages.Monitor("kind"), StateText(state), now, 0);

    /// <summary>
    /// The wording for one status. A held pair states its own reason (<see cref="PairAutoState.Reason"/> detail),
    /// so the window shows why it is held and not a generic word.
    /// </summary>
    public static string StateText(PairAutoState state) => state.Status switch
    {
        AutoStatus.Idle => EdgeMessages.Monitor("idle"),
        AutoStatus.Waiting => EdgeMessages.Monitor("waiting"),
        AutoStatus.Running => EdgeMessages.Monitor("running"),
        AutoStatus.Paused => EdgeMessages.Monitor("paused"),
        AutoStatus.Off => EdgeMessages.Monitor("off"),
        AutoStatus.Held => state.Reason?.Detail ?? EdgeMessages.Monitor("held"),
        // An undefined status (a stale saved value) reads as watching rather than throwing in a presentation path.
        _ => EdgeMessages.Monitor("idle")
    };
}
