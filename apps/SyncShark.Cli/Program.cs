using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Cli;

/// <summary>Connect the CLI to console streams and its detached log reader.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_,e) => { e.Cancel = true; stop.Cancel(); };
        var output = Engine.Edge.Cli.Decorate(Console.Out, error: false); var diagnostics = Engine.Edge.Cli.Decorate(Console.Error, error: true);
        try { return await Engine.Edge.Cli.RunAsync(args, new LogFiles(), output, diagnostics, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 1; }
        catch (Exception error) { await diagnostics.WriteLineAsync(error.Message); return 1; }
        finally { await output.FlushAsync(); await diagnostics.FlushAsync(); }
    }
}
