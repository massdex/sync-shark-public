namespace Massdex.SyncShark.Engine;

/// <summary>How a retired folder-removal operation releases runtime responsibility: NoEffect only when
/// every member effect is proven unsent, otherwise Acknowledged.</summary>
public enum FolderRetirementDisposition { Acknowledged, NoEffect }
