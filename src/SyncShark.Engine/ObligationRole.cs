namespace Massdex.SyncShark.Engine;

/// <summary>The semantic part of an unfinished branch that its obligation key separates.</summary>
public enum ObligationRole
{
    OldPath,
    NewPath,
    Content,
    Subtree
}
