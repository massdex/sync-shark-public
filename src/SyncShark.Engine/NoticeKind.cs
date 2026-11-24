namespace Massdex.SyncShark.Engine;

// Persisted by number in issue rows; values are a stored format.
/// <summary>The kinds of issue on a pair's issue list (D28).</summary>
public enum NoticeKind { Failure = 0, Tie = 1, NameClash = 2, Info = 3, Skipped = 4, DriveChange = 5, RootIdentity = 6, PutBack = 7 }
