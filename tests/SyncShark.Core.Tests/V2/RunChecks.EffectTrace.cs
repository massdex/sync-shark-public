namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private sealed class EffectTraceCapture : RunTrace
    {
        internal readonly List<string> Lines = [];
        internal override void WriteTrace(string line) => Lines.Add(line);
    }

    private static void EffectTraceCatalogCoverage()
    {
        var previous = RunTrace.Slot.Value;
        var trace = new EffectTraceCapture();
        try
        {
            RunTrace.Slot.Value = trace;
            var action = new SyncAction("effect", 1, ActionKind.CopyNew, Side.B, "file.txt",
                null, null, null, false, "object");
            foreach (var outcome in new[] { Outcome.Failed, Outcome.Uncertain })
            {
                var result = new ActionResult(action.ActionId, outcome, null, null, null, "provider\nrefused")
                    { FailureKind = FailureRetryOwner.FailureKind.ProviderThrottle };
                RunTrace.EffectResult(action, result);
                Require(trace.Lines.Last() == $"effect outcome={outcome} kind=CopyNew side=B path=\"file.txt\" category=ProviderThrottle reason=\"provider\\nrefused\"",
                    "real effect catalog formats outcome, kind, side, relative path, category and escaped reason");
            }
            Require(trace.Lines.Count == 2, "failed and uncertain effects each emit exactly one trace line");
            RunTrace.EffectResult(action, new(action.ActionId, Outcome.Refused, null, null, null, "guard refused"));
            Require(trace.Lines.Last().Contains("outcome=Refused", StringComparison.Ordinal), "refused effects are logged");
            RunTrace.EffectCommitFailed(action, new(action.ActionId, Outcome.Done, null, null, null, null),
                new IOException("commit unavailable"));
            Require(trace.Lines.Last().StartsWith("effect uncommitted outcome=Done kind=CopyNew side=B path=\"file.txt\"", StringComparison.Ordinal) &&
                trace.Lines.Last().Contains("IOException: commit unavailable", StringComparison.Ordinal),
                "failed result commits retain effect identity and explicitly uncommitted outcome");
        }
        finally { RunTrace.Slot.Value = previous; }
    }
}
