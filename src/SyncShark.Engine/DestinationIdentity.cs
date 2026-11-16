namespace Massdex.SyncShark.Engine;

/// <summary>The old destination owner's freshly observed identity disposition. MovingAncestor is an executor-only
/// dependency: a missing parent's identity is bound to an unapplied folder rename; the planner never receives it.</summary>
public enum DestinationIdentity { Gone, Moving, Here, MovingAncestor }
