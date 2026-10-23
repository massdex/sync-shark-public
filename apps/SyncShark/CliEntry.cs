using System.IO;
using System.Text;

namespace Massdex.SyncShark;

// Console entry point for `SyncShark.exe --cli <command>`. This file is only an adapter: argument
// parsing, the command contract, safe output projection and the exit-code mapping live in
// SyncShark.Core. It creates no window, no tray icon, no service, no listener and no second runtime.
internal static class CliEntry
{
    internal static bool IsCliInvocation(IReadOnlyList<string> args) => SyncCli.IsCliInvocation(args);

    internal static int Execute(string[] args)
    {
        AttachToParentConsoleIfNeeded();
        // Explicit UTF-8 without a byte-order mark, so a redirected stdout contains exactly the
        // JSON object or the human text and nothing else.
        var standardOutput = OpenStandard(Console.OpenStandardOutput);
        var standardError = OpenStandard(Console.OpenStandardError);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            // Ctrl+C is routed through the existing cancellation token rather than terminating the
            // process mid-effect: the run reaches its existing durable checkpoint, clean-up finishes
            // and this process then releases the exclusive product lock with exit code 130.
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        var registered = false;
        try
        {
            Console.CancelKeyPress += cancelHandler;
            registered = true;
        }
        catch (Exception)
        {
            // A detached process without a console cannot receive Ctrl+C; the command still runs.
            registered = false;
        }
        try
        {
            // The --core switch (or the SYNCSHARK_CORE setting) selects the core before anything else; a
            // usage error is reported without opening state. v2 skips the running-application bridge because
            // the running application owns the v1 core, not the fresh v2 store.
            var selection = Massdex.SyncShark.V2.CoreSwitch.Select(args,
                Environment.GetEnvironmentVariable(Massdex.SyncShark.V2.CoreSwitch.EnvironmentVariable));
            if (selection.Error is not null)
            {
                TryWrite(standardError, "Sync Shark (usage): " + selection.Error);
                return (int)SyncCliExitCode.Usage;
            }
            if (!selection.UseV2) TryWrite(standardError, Massdex.SyncShark.V2.CoreSwitch.LegacyNotice);
            args = selection.Args;
            if (!selection.UseV2 && args.Contains("autosync", StringComparer.Ordinal))
            {
                TryWrite(standardError, "AutoSync uses core v2. Core v1 remains selected and nothing was started. To switch explicitly, repeat with --cli --core v2 autosync <command>.");
                return (int)SyncCliExitCode.Usage;
            }
            if (!selection.UseV2 && (args.Contains("--plan-only", StringComparer.Ordinal) || args.Contains("--rehearse", StringComparer.Ordinal)))
            {
                // A plan-only preview must never fall back to the v1 core, whose runtime would open the live
                // product state. Refuse before any bridge attempt, lock or store open.
                TryWrite(standardError, "Sync Shark (usage): isolated run previews and rehearsals are available only with core v2. " +
                    "Repeat with --cli --core v2 and the same run options; nothing was opened or changed.");
                return (int)SyncCliExitCode.Usage;
            }
            if (selection.UseV2)
            {
                // An isolated preview is never sent to the live background host.
                if (!args.Contains("--plan-only", StringComparer.Ordinal) && !args.Contains("--rehearse", StringComparer.Ordinal))
                {
                    var hosted = Task.Run(() => BackgroundHostEntry.TryCliAsync(args, standardOutput, standardError,
                        cancellation.Token)).GetAwaiter().GetResult();
                    if (hosted is { } code) return code;
                }
                return Task.Run(() => SyncCli.ExecuteAsync(args, standardOutput, standardError,
                    () => Massdex.SyncShark.V2.V2CliRuntime.AcquireProduction(args), cancellation.Token)).GetAwaiter().GetResult();
            }
            // State-free commands never need a runtime. Everything else first asks the running
            // application, so the CLI works while the window or tray runtime owns the product, and only
            // falls back to the direct lock-owning path when no application is running.
            if (!SyncCli.IsStateFreeInvocation(args))
            {
                using var bridge = new SyncCliBridgeClient();
                if (bridge.TryInvoke(args, standardError, TimeSpan.FromMinutes(30), cancellation.Token,
                        out var bridgeExitCode, out var bridgeText, out var bridgeError))
                {
                    if (bridgeText.Length > 0) TryWriteRaw(standardOutput, bridgeText);
                    if (bridgeError is not null) TryWrite(standardError, "Sync Shark: " + bridgeError);
                    return bridgeExitCode;
                }
            }
            return Task.Run(() => SyncCli.ExecuteAsync(args, standardOutput, standardError,
                SyncCliRuntime.AcquireProduction, cancellation.Token)).GetAwaiter().GetResult();
        }
        catch (SyncCliUsageException error)
        {
            TryWrite(standardError, "Sync Shark (usage): " + error.Message);
            return (int)SyncCliExitCode.Usage;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Ctrl+C while waiting on the running application stops this client only: the owner's job keeps its
            // own state and is never cancelled or replayed from here.
            TryWrite(standardError, "Sync Shark (canceled): stopped waiting for the running application; the "
                + "requested job was not cancelled or replayed. Run 'status' before retrying.");
            return (int)SyncCliExitCode.Canceled;
        }
        catch (Exception error)
        {
            TryWrite(standardError, "Sync Shark (cli_failure): " + SyncCli.DescribeException(error));
            return (int)SyncCliExitCode.Failed;
        }
        finally
        {
            if (registered)
            {
                try { Console.CancelKeyPress -= cancelHandler; } catch (Exception) { }
            }
            TryFlush(standardOutput);
            TryFlush(standardError);
        }
    }

    private static TextWriter OpenStandard(Func<Stream> open)
    {
        try
        {
            return new StreamWriter(open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }
        catch (Exception)
        {
            // Launched without a console and without redirection: nothing can be read back.
            return TextWriter.Null;
        }
    }

    private static void TryWrite(TextWriter writer, string text)
    {
        try { writer.WriteLine(text); } catch (Exception) { }
    }

    private static void TryWriteRaw(TextWriter writer, string text)
    {
        try { writer.Write(text); } catch (Exception) { }
    }

    // A Windows GUI executable has no console of its own. When it is launched from a terminal with neither
    // stream redirected, attaching to the parent console is what makes CLI output visible and delivers Ctrl+C.
    // A redirected invocation keeps its inherited pipes untouched, and ordinary GUI startup never reaches this
    // code, so no console window is created for normal application use.
    private static void AttachToParentConsoleIfNeeded()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (GetConsoleWindow() != IntPtr.Zero) return;
            // Attach whenever this GUI process has no console at all - including the case where a missing
            // handle made Console.IsOutputRedirected report true. Attaching never rewrites an inherited
            // pipe or file handle, so each redirected stream is preserved independently.
            AttachConsole(-1);
        }
        catch (Exception)
        {
            // Attaching is best effort: without it the process still runs as a windowless CLI.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static void TryFlush(TextWriter writer)
    {
        try { writer.Flush(); } catch (Exception) { }
    }
}
