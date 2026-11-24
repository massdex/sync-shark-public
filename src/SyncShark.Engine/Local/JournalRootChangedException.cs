namespace Massdex.SyncShark.Engine.Local;

/// <summary>Intake held the source prefix because the configured root itself changed.</summary>
public sealed class JournalRootChangedException(string root) : ChangedUnderneathException(root);
