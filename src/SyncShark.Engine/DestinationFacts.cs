namespace Massdex.SyncShark.Engine;

/// <summary>One completely discovered destination binding and its fresh identity disposition, not deletion authority.</summary>
public sealed record DestinationFacts(SyncedEntry Binding, Side Side, DestinationIdentity Identity);
