namespace Massdex.SyncShark.Engine.Run;

/// <summary>Unsent arrivals at one endpoint, kept in receipt order behind one boundary.</summary>
internal sealed class MoveRun(WorkInput first) { internal WorkInput? Head=first; internal WorkInput Tail=first; }
