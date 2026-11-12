namespace Massdex.SyncShark.Engine;

/// <summary>Distinguishes selected provider effects from state-only folder reconciliation.</summary>
public enum FolderEffectKind
{
    None,
    RecursiveFolder,
    ConditionalFile,
    StateOnly
}
