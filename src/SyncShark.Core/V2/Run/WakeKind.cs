namespace Massdex.SyncShark.V2;

/// <summary>What ended a <see cref="ChangeWait"/>.</summary>
internal enum WakeKind { Signal, Timeout, Kick, Fault }
