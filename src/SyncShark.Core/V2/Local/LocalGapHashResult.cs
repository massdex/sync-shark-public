namespace Massdex.SyncShark.V2;

internal sealed record LocalGapHashResult(LocalGapHashOutcome Outcome, string? Hash = null, LocalGapProof? Proof = null,
    string? Reason = null, int? QuarantineCode = null)
{
    internal ContentProofFailure? Failure { get; init; }
    internal string? Diagnostic => Failure?.Encode() ?? Reason;
    internal LocalGapHashResult At(ContentProofFailure.StageKind stage, int? nativeError = null)
    {
        var failure = ContentProofFailure.FromDiagnostic(stage, Reason ?? MessageCatalog.Default.Message("proof-required"));
        return this with { Failure = failure with { NativeError = nativeError ?? failure.NativeError } };
    }
    internal static LocalGapHashResult Failed(ContentProofFailure.StageKind stage, string cause, int? nativeError = null,
        LocalGapHashOutcome outcome = LocalGapHashOutcome.Unverifiable) =>
        new LocalGapHashResult(outcome, Reason: cause).At(stage, nativeError);
}
