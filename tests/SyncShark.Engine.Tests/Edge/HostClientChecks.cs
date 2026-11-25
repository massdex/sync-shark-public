using System.IO.Pipes;
using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostClientChecks
{
    public static async Task RunAsync()
    {
        var result = await new HostClient().SendAsync(["status"], TextWriter.Null, TextWriter.Null, default);
        Check.Require(result.ExitCode is null && !result.ReceivedExit, "An absent host did not return definite pre-send absence");

        // A long-lived host must not keep the caller's pipe open after the CLI exits.
        using var caller = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var probe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Check.Require(await HostClient.StartAsync(probe, "/d /c \"echo 1&ping -n 30 127.0.0.1 >nul\"", default) == 0,
            "The probe's readiness line was not read");
        caller.DisposeLocalCopyOfClientHandle();
        var eof = Task.Run(() => caller.Read(new byte[1], 0, 1));
        Check.Require(await Task.WhenAny(eof, Task.Delay(TimeSpan.FromSeconds(10))) == eof && await eof == 0,
            "The launched host inherited the caller's pipe");
    }
}
