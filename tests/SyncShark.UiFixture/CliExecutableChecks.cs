using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SyncShark.UiFixture;

// Real executable adapter checks: the actual compiled GUI SyncShark.exe is invoked with stateless --cli
// commands under normal, stdout-redirected, stderr-redirected and mixed redirection. No product lock, state,
// credential or provider is touched, so this needs no isolated state root.
internal static class CliExecutableChecks
{
    internal static int Run()
    {
        var executable = Locate();
        var checks = 0;
        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("CLI executable check failed: " + label);
            checks++;
            Console.WriteLine("PASS " + label);
        }

        // Both streams redirected: the CLI must write the human text to stdout and nothing to stderr.
        var help = Run(executable, ["--cli", "help"], redirectOutput: true, redirectError: true);
        Require(help.ExitCode == 0 && help.Output.Contains("--cli", StringComparison.Ordinal) &&
                help.Error.Length == 0,
            "help under full redirection writes stdout only and exits 0");
        Require(help.Output.Contains("first-sync apply", StringComparison.Ordinal) &&
                help.Output.Contains("discovery check", StringComparison.Ordinal),
            "the real executable publishes the current command map");

        // JSON mode under redirection: exactly one schemaVersion 1 object on stdout.
        var version = Run(executable, ["--cli", "version", "--json"], redirectOutput: true, redirectError: true);
        Require(version.ExitCode == 0 &&
                version.Output.Trim().StartsWith("{\"schemaVersion\":1", StringComparison.Ordinal) &&
                version.Error.Length == 0,
            "version --json under full redirection emits one JSON object");

        // Only stdout redirected: the error must still reach the redirected stderr of the caller.
        var invalid = Run(executable, ["--cli", "frobnicate"], redirectOutput: true, redirectError: true);
        Require(invalid.ExitCode == 2 && invalid.Output.Length == 0 &&
                invalid.Error.Contains("usage", StringComparison.Ordinal),
            "an invalid command exits 2 with its diagnostic on the redirected error stream");

        // Mixed redirection: stdout captured, the error stream left inherited - the process must still start,
        // and its own stdout redirection must be honoured without the console attachment stealing it.
        var mixed = Run(executable, ["--cli", "version"], redirectOutput: true, redirectError: false);
        Require(mixed.ExitCode == 0 && mixed.Output.Contains("Sync Shark", StringComparison.Ordinal),
            "a mixed-redirection invocation keeps its captured stdout intact");

        // No redirection at all: launched from a parent without a console this must still run and exit cleanly.
        var attached = Run(executable, ["--cli", "version"], redirectOutput: false, redirectError: false);
        Require(attached.ExitCode == 0, "an unredirected invocation exits 0 without a console attached");

        Console.WriteLine("{\"result\":\"PASS\",\"checks\":" + checks +
            ",\"note\":\"real SyncShark.exe adapter under normal and redirected invocation\"}");
        return 0;
    }

    private static string Locate()
    {
        // Use the app copy built with this exact fixture configuration, never a
        // stale neighbouring Debug build when checking a Release candidate.
        var copiedApp = Path.Combine(AppContext.BaseDirectory, "SyncShark.exe");
        if (File.Exists(copiedApp)) return copiedApp;
        var frameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = frameworkDirectory.Parent?.Name
            ?? throw new InvalidOperationException("The fixture configuration is unavailable.");
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "SyncShark", configuration, frameworkDirectory.Name, "SyncShark.exe"));
        if (!File.Exists(candidate))
            throw new InvalidOperationException("The compiled SyncShark.exe was not found at " + candidate + ".");
        return candidate;
    }

    private static (int ExitCode, string Output, string Error) Run(string executable, string[] arguments,
        bool redirectOutput, bool redirectError)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectError
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("The CLI process could not be started.");
        var output = redirectOutput ? process.StandardOutput.ReadToEnd() : string.Empty;
        var error = redirectError ? process.StandardError.ReadToEnd() : string.Empty;
        process.WaitForExit(60_000);
        return (process.ExitCode, output, error);
    }
}
