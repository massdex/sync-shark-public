namespace Massdex.SyncShark.Engine;

/// <summary>Which owner consumes a request attempt's completed result (READING 8.5).</summary>
public enum AttemptOwner { Listener, Prerequisite, Request, Transfer, Recovery }
