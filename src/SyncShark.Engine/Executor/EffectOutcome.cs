namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Only an applied effect permits its caller to settle a notice or remove a source.</summary>
public enum EffectOutcome { Applied, Pending, Refused }
