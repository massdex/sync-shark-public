namespace Massdex.SyncShark.V2;

/// <summary>Completed measurements of one stage, including failed and cancelled operations.</summary>
public sealed record RunTiming(long Count, TimeSpan Elapsed, TimeSpan Maximum);
