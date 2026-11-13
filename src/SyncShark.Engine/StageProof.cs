namespace Massdex.SyncShark.Engine;

/// <summary>Exact identity and content proof of one file: an owned stage, or a receipt observed as the victim of a wrong restore.
/// Publication and recovery compare against this object, never size alone.</summary>
public sealed record StageProof(string Path, string VolumeId, string FileId, long Size,
    string ContentHash);
