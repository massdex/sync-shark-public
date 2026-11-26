namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Only an applied effect permits its caller to settle a notice or remove a source.</summary>
/// <remarks>Accepted: Dropbox took the write as a saved batch job; the input is released and the job checker settles the effect.</remarks>
public enum EffectOutcome { Applied, Pending, Refused, Accepted }
