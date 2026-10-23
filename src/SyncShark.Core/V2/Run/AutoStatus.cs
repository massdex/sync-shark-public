namespace Massdex.SyncShark.V2;

/// <summary>Where one pair stands in continuous sync.</summary>
public enum AutoStatus
{
    /// <summary>Watching; nothing pending.</summary>
    Idle,

    /// <summary>A change was seen and is settling, or a retry is scheduled (see <see cref="PairAutoState.RetryAt"/>).</summary>
    Waiting,

    /// <summary>An automatic run is in progress.</summary>
    Running,

    /// <summary>Sync is paused for this pair (global or account pause).</summary>
    Paused,

    /// <summary>The pair is turned off.</summary>
    Off,

    /// <summary>Stopped until the user acts; <see cref="PairAutoState.Reason"/> says why.</summary>
    Held
}
