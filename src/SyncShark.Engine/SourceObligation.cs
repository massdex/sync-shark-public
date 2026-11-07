namespace Massdex.SyncShark.Engine;

/// <summary>Why a source still owes reconciliation. LocalSkippedCoverage outranks the others until its matching
/// completion.</summary>
public enum SourceObligation { Bootstrap, NormalRepair, Configuration, LocalSkippedCoverage }

/// <summary>The one precedence rule between a source's owed reason and a newly owed one.</summary>
internal static class SourceObligations
{
    /// <summary>A new reason replaces no debt or Configuration debt, the weakest, which admits live Local writes; any
    /// other debt already covers it, so neither a loss after a configuration change nor a configuration change after a
    /// loss lifts the Local-write refusal (R6-F1, R6-1).</summary>
    internal static SourceObligation Owed(SourceObligation? owed, SourceObligation next) =>
        owed is null or SourceObligation.Configuration ? next : owed.Value;
}
