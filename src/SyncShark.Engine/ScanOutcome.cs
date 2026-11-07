namespace Massdex.SyncShark.Engine;

/// <summary>Whether unresolved ancestor work refused an effect during the completed scan's sync pass.</summary>
public sealed record ScanOutcome(bool Refused);
