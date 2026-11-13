namespace Massdex.SyncShark.Engine;

/// <summary>What a pair's pending slot holds: no note, a validated current note or an unverified legacy note.
/// Storage alone decodes it; every consumer matches each variant explicitly, so no null default bypasses legacy manual access.</summary>
public abstract record PendingRead;
