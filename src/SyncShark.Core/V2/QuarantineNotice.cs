namespace Massdex.SyncShark.V2;

/// <summary>The exact entities protected by a confirmed antivirus I/O failure, not an inferred deletion.</summary>
public sealed record QuarantineNotice(Side Target, ItemState? Source, ItemState? ExpectedTarget, bool ScanOnly = false, string? PendingActionId = null, string? ObjectKey = null, ItemState? HeldSource = null, string? GrantedExclusionPath = null, string? GrantedStagePath = null, string? ConflictingGrantId = null);
