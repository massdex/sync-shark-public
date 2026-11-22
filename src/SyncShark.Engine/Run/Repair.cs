namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns one Repair's acquisition, dependent work and source completion.</summary>
public static class Repair
{
    /// <summary>Admit independently discovered comparison rows and whole rename groups to the pair queue for ItemSync. A physical-hop comparison is part of its running parent, not competing queued work. With Notices, never settle PutBack from a comparison, even when the path has a new occupant. Only its own Retry or a later change of the same item can settle it. Use the saved parent mapping for missing comparison endpoints. Withhold coverage only under unreadable children and continue siblings. Publish completed discovery before spending deleted names. Consume ordered rename candidates of this acquisition before ordinary rows; a physical-hop handoff with proven old-range coverage does no new listing. Otherwise compare the observed endpoints against the saved logical binding before allowing the next leg. Publish only completed history and revision-matched completion.</summary>
    public static Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, Scope? scope, (SyncedEntry Binding, Scope Before, Scope After)? physicalHop, CancellationToken ct) => throw new NotImplementedException();
}
