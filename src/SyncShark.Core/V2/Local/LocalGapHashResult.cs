namespace Massdex.SyncShark.V2;

internal sealed record LocalGapHashResult(LocalGapHashOutcome Outcome, string? Hash = null, LocalGapProof? Proof = null, string? Reason = null, int? QuarantineCode = null);
