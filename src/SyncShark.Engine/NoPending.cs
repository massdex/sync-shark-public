namespace Massdex.SyncShark.Engine;

/// <summary>The pair's pending slot holds no note (C-10). Storage returns it for SQL NULL only;
/// an unknown note shape is a corruption refusal, never NoPending.</summary>
public sealed record NoPending : PendingRead;
