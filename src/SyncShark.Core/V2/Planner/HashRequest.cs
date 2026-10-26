namespace Massdex.SyncShark.V2;

/// <summary>The paths per side whose hashes a run must compute before deciding (planner.md section 3).</summary>
public sealed record HashRequest(IReadOnlyList<string> PathsA, IReadOnlyList<string> PathsB);
