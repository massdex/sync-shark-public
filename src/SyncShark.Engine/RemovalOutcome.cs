namespace Massdex.SyncShark.Engine;

/// <summary>The classified result of one Local removal or publication (C-10); only a verified permitted removal authorizes publication or settlement.</summary>
public enum RemovalOutcome { NoEffect, Removed, Published, WrongRestored, Retained, Unknown }
