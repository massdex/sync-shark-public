namespace Massdex.SyncShark.V2;

// All observational output, including the error reporter, is non-throwing.
internal static class RunOutput
{
    internal static void Write(Action<string> sink, string line)
    {
        try { sink(line); }
        catch (Exception error) { Observe(error); }
    }

    internal static void Observe(Exception error)
    {
        try { Console.Error.WriteLine("Sync Shark trace: run output failed: " + error.Message); }
        catch { /* Output must never change the run's outcome. */ }
    }
}
