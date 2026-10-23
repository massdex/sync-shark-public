using Massdex.SyncShark;

internal static partial class CliChecks
{
    internal static void CheckActionLifetimePresentation(SyncActivityRetentionWindow window)
    {
        var data=new SyncCliActivityListData([],25,0,0,false,HistoryWindow:window);
        var text=SyncCli.RenderHuman(data);
        if(window.Actions is not { } journal ||
            !text.Contains($"{journal.EncodedBytes:N0} encoded bytes",StringComparison.Ordinal) ||
            !text.Contains($"{journal.SafetyHeldActions:N0} unclosed actions",StringComparison.Ordinal) ||
            journal.Warning && !text.Contains("do not pause synchronization",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The actual CLI activity renderer omitted exact journal accounting or its nonblocking warning.");
        Console.WriteLine("PASS actual CLI activity renderer preserves journal bytes, held counts and nonblocking warning");
    }
}
