namespace Massdex.SyncShark.V2;

/// <summary>One entity's deferral row: its state, when it was last touched, and its review or held batch.</summary>
public readonly record struct DefenderDeferralState(DefenderConsent Consent, DateTimeOffset Updated, string? Batch);
