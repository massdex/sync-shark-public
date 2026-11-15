namespace Massdex.SyncShark.Engine;

/// <summary>Why a source still owes reconciliation. LocalSkippedCoverage outranks the others until its matching
/// completion.</summary>
public enum SourceObligation { Bootstrap, NormalRepair, Configuration, LocalSkippedCoverage }
