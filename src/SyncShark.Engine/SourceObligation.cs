namespace Massdex.SyncShark.Engine;

/// <summary>Why a source still owes reconciliation. LocalSkippedCoverage outranks the others until its matching
/// completion.</summary>
public enum SourceObligation { Bootstrap, NormalRepair, Configuration, LocalSkippedCoverage }

/// <summary>What owed Repair refuses in live work: nothing, the whole pair, or only effects that write Local.</summary>
public enum LiveRefusal { None, Pair, LocalWrites }
