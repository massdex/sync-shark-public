using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine;

/// <summary>A transient host observation, never persisted health or proof of progress. Edge owns its text.</summary>
public sealed record HostStatusUpdate(AutoStatus? Status, string? Detail, string? Message);
