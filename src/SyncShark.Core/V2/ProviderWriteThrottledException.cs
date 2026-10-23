namespace Massdex.SyncShark.V2;

/// <summary>A proven unapplied write deferred to a later run, not a changed-target refusal.</summary>
public sealed class ProviderWriteThrottledException(string message, Exception inner) : Exception(message, inner);
