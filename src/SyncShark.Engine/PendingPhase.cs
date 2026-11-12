namespace Massdex.SyncShark.Engine;

/// <summary>The durable stage of one local pending removal (C-10); the Local recovery owner interprets it and Storage alone encodes it.</summary>
public enum PendingPhase
{
    Armed,
    Recycled,
    PermanentArmed,
    Removed,
    RestoreArmed,
    WrongRestored,
    Retained,
    PublishArmed,
    Published,
    Unknown,
    DiscardArmed
}
