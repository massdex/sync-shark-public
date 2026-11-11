namespace Massdex.SyncShark.Engine;

/// <summary>The kinds of issue on a pair's issue list (D28).</summary>
public enum IssueKind { Failure, Tie, NameClash, Info, Skipped, DriveChange = 5, RootIdentity = 6 }
