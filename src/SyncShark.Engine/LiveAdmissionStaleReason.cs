namespace Massdex.SyncShark.Engine;

/// <summary>Why a live admission's captured coverage, selection, relevant evidence, footprint or baseline
/// no longer authorizes its effect (LIVE 4).</summary>
internal enum LiveAdmissionStaleReason { CoverageChanged, RelevantEvidenceChanged, SelectionChanged,
    FootprintChanged, BaselineChanged, SelectionDestinationOccupied }
