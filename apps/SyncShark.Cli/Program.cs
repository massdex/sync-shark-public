using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Cli;

/// <summary>Connect the CLI to console streams and its detached log reader.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_,e) => { e.Cancel = true; stop.Cancel(); };
        try { return await Engine.Edge.Cli.RunAsync(args, new LogFiles(), Console.Out, Console.Error, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 1; }
        catch (Exception error) { await Console.Error.WriteLineAsync(error.Message); return 1; }
    }
}
