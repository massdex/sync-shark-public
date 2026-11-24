namespace Massdex.SyncShark.Engine.Local;

/// <summary>One physical NTFS record with the full file and historical parent references.</summary>
public sealed record UsnRecord(ulong FileId, ulong ParentId, ulong Usn, uint Reason, FileAttributes Attributes, string? Name);
