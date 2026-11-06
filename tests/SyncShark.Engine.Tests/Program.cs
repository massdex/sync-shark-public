using Massdex.SyncShark;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Tests;

// --cli engine ...: the app's engine routing, without building the app (live test pair).
if (args is [CheckArguments.CliFlag, ..])
    return await SyncCli.ExecuteAsync(args, Console.Out, Console.Error, () => new EngineCliRuntime());

// --engine-checks <name>: runs that check's files; exit 0 on success, 1 with the failure printed.
if (args is not [var flag, var name] || flag != CheckArguments.Flag || !CheckRegistry.All.TryGetValue(name, out var checks))
{
    Console.Error.WriteLine(string.Join(CheckArguments.ListSeparator, CheckRegistry.All.Keys));
    return CheckArguments.UsageExit;
}
// Every member runs, so one unfinished file does not hide the others; each failure is printed with its member.
var failures = 0;
foreach (var check in checks)
{
    try
    {
        Check.ResetSkip();
        check();
        Console.WriteLine((Check.Skipped ? "SKIP " : "PASS ") + check.Method.DeclaringType?.Name + "." +
            check.Method.Name);
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + check.Method.DeclaringType?.Name + "." + check.Method.Name + ": " + error);
    }
}
return failures == 0 ? CheckArguments.Success : CheckArguments.Failure;
