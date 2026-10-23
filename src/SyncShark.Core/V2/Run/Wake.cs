namespace Massdex.SyncShark.V2;

/// <summary>Why a wait ended; <see cref="Error"/> is the dead watch's error for <see cref="WakeKind.Fault"/>.</summary>
internal readonly record struct Wake(WakeKind Kind, Exception? Error = null);
