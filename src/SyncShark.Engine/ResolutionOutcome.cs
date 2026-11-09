namespace Massdex.SyncShark.Engine;

/// <summary>One resolution's combined outcome. Resolved comes from the lifecycle owner: the notice closed, or was
/// dismissed by the dismiss choice (7.9).</summary>
public sealed record ResolutionOutcome(SyncOutcome Outcome, bool Resolved);
