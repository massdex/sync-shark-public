namespace Massdex.SyncShark;

/// <summary>Stable identity of one local directory: volume serial number, file id and the optional high half of a wide file id.
/// A null high half is basic format (volume serial within UInt32); a high half of zero is still a wide identity.</summary>
public sealed record DirectoryIdentity(ulong VolumeSerialNumber, ulong FileId, ulong? FileIdHigh);
