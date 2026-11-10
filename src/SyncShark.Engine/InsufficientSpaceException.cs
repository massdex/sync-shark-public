namespace Massdex.SyncShark.Engine;

/// <summary>A write stopped because its volume is full. Storage wraps SQLite FULL in it; the cause stays inner.</summary>
public sealed class InsufficientSpaceException(Exception inner)
    : Exception(Text.Get(TextKey.RepairStoppedNoSpace), inner);
