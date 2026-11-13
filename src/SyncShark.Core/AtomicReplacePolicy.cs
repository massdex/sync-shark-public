namespace Massdex.SyncShark;

/// <summary>The bounded wait before each retry of a same-directory publish (#135). The number of waits is
/// the retry bound; the values come from the product settings, never from this type.</summary>
public sealed record AtomicReplacePolicy(IReadOnlyList<TimeSpan> Delays);
