namespace Massdex.SyncShark.Engine;

/// <summary>The one exceptional conflict proof: exact size, modified UTC ticks and content hash.</summary>
public sealed record LocalHashProof(long Size, long ModifiedTicks, string Hash);
