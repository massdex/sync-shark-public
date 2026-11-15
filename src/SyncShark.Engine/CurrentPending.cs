namespace Massdex.SyncShark.Engine;

/// <summary>The pair's pending slot holds a fully validated current note (C-10). Storage returns it only after
/// version, phase and field validation; an unknown shape or version is a corruption refusal, never NoPending.</summary>
public sealed record CurrentPending(PendingEffect Value) : PendingRead;
