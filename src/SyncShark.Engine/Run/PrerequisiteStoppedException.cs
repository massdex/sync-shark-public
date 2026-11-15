namespace Massdex.SyncShark.Engine.Run;

/// <summary>The host's classified caller stop of one prerequisite attempt, which counts nothing (engine.md 5.6).
/// The classification travels with the exception instead of being inferred from mutable pause state.</summary>
internal sealed class PrerequisiteStoppedException(OperationCanceledException cancellation)
    : OperationCanceledException(cancellation.Message, cancellation, cancellation.CancellationToken);
