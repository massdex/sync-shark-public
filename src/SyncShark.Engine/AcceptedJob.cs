namespace Massdex.SyncShark.Engine;

/// <summary>A returned job and its original wire-ordered subjects.</summary>
public sealed record AcceptedJob(string PairId, string Route, string JobId, IReadOnlyList<JobMember> Launch);
