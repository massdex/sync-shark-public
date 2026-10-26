namespace Massdex.SyncShark.V2;

internal sealed record LocalGapProof(string JournalCursor, NtfsFileUsn File);
