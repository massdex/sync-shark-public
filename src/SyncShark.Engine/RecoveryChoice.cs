namespace Massdex.SyncShark.Engine;

/// <summary>The exact-operation choice for a pending local removal (C-10); only the Local recovery owner gives each its effect.</summary>
public enum RecoveryChoice { Check, AcceptCurrent, AbandonIncoming }
