namespace Massdex.SyncShark.Engine;

/// <summary>How one failed item effect settles, decided once by the provider adapter's classifier.
/// Permanent is the default, so an unclassified failure keeps today's stored Failure.</summary>
public enum FailureDisposition
{
    /// <summary>A definite refusal: the existing specific settlement.</summary>
    Permanent,
    /// <summary>A dispatched write whose effect is not proven either way: a stored, public Failure, never resent.</summary>
    UnknownEffect,
    /// <summary>A temporary failure of a write proven to have had no effect: retained work and a timed retry, no Issue.</summary>
    TemporaryNoEffect
}
