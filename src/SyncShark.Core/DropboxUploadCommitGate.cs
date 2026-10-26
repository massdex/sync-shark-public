using System.Collections.Concurrent;
using System.Diagnostics;

namespace Massdex.SyncShark;

/// <summary>Bounds namespace write batches across all bound clients, not independently per client or settings value.</summary>
internal static class DropboxUploadCommitGate
{
    private static readonly ConcurrentDictionary<string, Binding> Gates = new(StringComparer.Ordinal);

    // A namespace's first binding fixes its capacity for this process. Changing settings never creates a
    // second gate for the same namespace; restart the process to apply a different namespace capacity.
    internal static SemaphoreSlim For(string account, string rootNamespace, int concurrency)
    {
        if (concurrency is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(concurrency));
        var key = account + "\n" + rootNamespace;
        var binding = Gates.GetOrAdd(key, _ => new Binding(new SemaphoreSlim(concurrency, concurrency), concurrency));
        // The first caller fixes the namespace's capacity; a later caller asking a different capacity still
        // shares the bound gate. Diagnose the mismatch so a changed setting is visible rather than silently
        // ignored. Both values are named; the account and namespace are never logged.
        if (binding.Concurrency != concurrency)
            Trace.TraceWarning(
                "Upload commit concurrency for a namespace is already bound to {0}; a caller requested {1}. " +
                "The bound value is kept; restart the process to apply a different namespace capacity.",
                binding.Concurrency, concurrency);
        return binding.Gate;
    }

    private sealed record Binding(SemaphoreSlim Gate, int Concurrency);
}
