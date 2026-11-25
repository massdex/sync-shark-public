namespace Massdex.SyncShark.Engine;

/// <summary>An excluded path on one side (D26). Keep: exclude without removing anything.</summary>
public sealed record Exclusion(Side Side, string Path, bool Keep);
