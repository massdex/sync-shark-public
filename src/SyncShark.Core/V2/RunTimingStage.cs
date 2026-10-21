namespace Massdex.SyncShark.V2;

/// <summary>Measured execution stages; elapsed totals sum operations and can exceed run wall time.</summary>
public enum RunTimingStage
{
    AdmissionWait,
    Staging,
    UploadSessionStart,
    BatchWait,
    ResultCommit,
    LocalHash,
    LocalRecheck
}
