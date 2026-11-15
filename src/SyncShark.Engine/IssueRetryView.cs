namespace Massdex.SyncShark.Engine;

/// <summary>One issue's retry presentation state (CV-RETRYVIEW, section 12.1): the exact value Run produces
/// from current authority for Edge to render. It carries no RetryAfter or other timing state.</summary>
public sealed record IssueRetryView(RetryPresentationState State);
