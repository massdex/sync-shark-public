using Massdex.SyncShark;

// This diagnostic is never an implicit primary-account or legacy-pair reader.
// Reuse the product CLI's exact-account/provider/credential routing rather than
// owning a second browser, credential lookup or folder-selection mechanism.
const string usage =
    "Usage: SyncShark.LiveBrowserProbe --allow-live-read --account <exact-connection-id> " +
    "[--path <remote-folder>] [--search <text>] [--json]";
if (args is ["--help"])
{
    Console.WriteLine(usage);
    Console.WriteLine("Read-only provider browsing; explicit live-read authority is required. " +
        "No implicit account, retained pair, sync, provider write or automatic retry.");
    return 0;
}
if (args.Length == 0 || args[0] != "--allow-live-read")
{
    Console.Error.WriteLine("Live browser probe refused before opening product state or credentials. " + usage);
    return (int)SyncCliExitCode.Usage;
}
var command = new[] { "--cli", "pairs", "folders" }.Concat(args.Skip(1)).ToArray();
try
{
    var invocation = SyncCliRegistry.Parse(command);
    if (string.IsNullOrWhiteSpace(invocation.AccountId))
        throw new SyncCliUsageException("An explicit exact --account is required; no primary-account fallback.");
}
catch (SyncCliException error)
{
    Console.Error.WriteLine("Live browser probe refused before opening product state or credentials: " +
        SyncCli.DescribeException(error));
    return (int)SyncCliExitCode.Usage;
}
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancel;
try
{
    return await SyncCli.ExecuteAsync(command, Console.Out, Console.Error,
        SyncCliRuntime.AcquireProduction, cancellation.Token);
}
finally { Console.CancelKeyPress -= cancel; }
