namespace Massdex.SyncShark.Engine.Planner;

/// <summary>The D15 outcome of comparing two modified times against the run's start.</summary>
public enum TimeOrder { Unreliable, Equal, LeftNewer, RightNewer }
