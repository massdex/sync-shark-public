namespace Massdex.SyncShark.V2;

/// <summary>What ended a <see cref="ChangeWait"/>.</summary>
internal enum WakeKind { Signal, Timeout, Kick, Fault }

/// <summary>Why a wait ended; <see cref="Error"/> is the dead watch's error for <see cref="WakeKind.Fault"/>.</summary>
internal readonly record struct Wake(WakeKind Kind, Exception? Error = null);
