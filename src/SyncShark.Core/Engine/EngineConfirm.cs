namespace Massdex.SyncShark;

/// <summary>An engine command's confirmation rule: always needs --yes, never does, or previews without it.</summary>
public enum EngineConfirm { Always, Never, Preview }
