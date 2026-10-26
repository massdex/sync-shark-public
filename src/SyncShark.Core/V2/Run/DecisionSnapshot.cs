namespace Massdex.SyncShark.V2;

/// <summary>One open needs-decision path observed fresh from both endpoints under the run gate (decision 64).</summary>
internal sealed record DecisionSnapshot(string NoticeId, string Path, ItemState? A, ItemState? B, string Token);
