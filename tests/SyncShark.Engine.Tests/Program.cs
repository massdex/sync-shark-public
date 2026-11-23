namespace Massdex.SyncShark.Engine.Tests;

/// <summary>Runs every named check and separates incomplete implementations from wrong outcomes.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if(args is ["--literal-sites", var candidate, var release, var output]) { Layout.LiteralsChecks.WriteLiteralSites(candidate,release,output); return 0; }
        var checks = CheckRegistry.All(CheckArguments.Parse(args));
        int passed = 0, missing = 0, failed = 0;
        foreach (var check in checks)
        {
            try { await check.Run(); ++passed; Console.WriteLine($"PASS {check.Name}"); }
            catch (NotImplementedException) { ++missing; Console.WriteLine($"NOT IMPLEMENTED {check.Name}"); }
            catch (Exception error) { ++failed; Console.Error.WriteLine($"FAIL {check.Name}: {error}"); }
        }
        Console.WriteLine($"passed={passed} not-implemented={missing} failed={failed} total={checks.Count}");
        return failed == 0 ? 0 : 1;
    }
}
