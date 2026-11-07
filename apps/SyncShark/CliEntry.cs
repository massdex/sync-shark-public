using System.IO;
using System.Text;
using Massdex.SyncShark.Engine;

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
        var json = Array.Exists(args, arg => string.Equals(arg, SyncCli.JsonSwitch, StringComparison.Ordinal));
        var width = OutputWidth();
        var standardOutput = OpenStandard(Console.OpenStandardOutput);
        // Colour is a client-terminal decoration only: human stdout and stderr are decorated here, while
        // a JSON result or a redirected stream stays plain (R1 review 4). Re-wrapping to the client width is
        // independent of colour, so NO_COLOR or a terminal without VT still wraps (R1 review 6).
        if (!json) standardOutput = MaybeHuman(standardOutput, output: true, width);
        var standardError = MaybeHuman(OpenStandard(Console.OpenStandardError), output: false, width);
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
            // Engine commands have their own runtime and state: they never reach the core switch or the app bridge.
            if (args.Skip(1).FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ==
                EngineCliCatalog.Group)
            {
                // A verified running Engine host answers first; when none does, the four-argument entry leaves a
                // pre-existing command to the existing standalone runtime, which also refuses a host-only command
                // rather than acquiring the product lock locally.
                var hosted = Task.Run(() => EngineHostEntry.TryCliAsync(args, standardOutput, standardError,
                    cancellation.Token)).GetAwaiter().GetResult();
                if (hosted is { } hostExitCode) return hostExitCode;
                return Task.Run(() => SyncCli.ExecuteAsync(args, standardOutput, standardError,
                    () => new Massdex.SyncShark.Engine.Edge.EngineCliRuntime(width), cancellation.Token)).GetAwaiter().GetResult();
            }
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
                    // The Engine host owns these four reads (bug #92): its pipe is tried before the retired
                    // AutoSync pipe, and a null answer keeps the existing direct path below.
                    var read = Task.Run(() => EngineHostEntry.TryReadAsync(args, standardOutput, standardError,
                        cancellation.Token)).GetAwaiter().GetResult();
                    if (read is { } readCode) return readCode;
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
            RestoreConsoleMode();
        }
    }

    // The human line budget follows the actual terminal, never wider than the shared maximum.
    private static int OutputWidth()
    {
        try
        {
            if (Console.IsOutputRedirected) return Massdex.SyncShark.Engine.Run.RunLog.HumanWidth;
            var width = Console.WindowWidth;
            return width <= 0 ? Massdex.SyncShark.Engine.Run.RunLog.HumanWidth
                : Math.Min(width, Massdex.SyncShark.Engine.Run.RunLog.HumanWidth);
        }
        catch (Exception)
        {
            return Massdex.SyncShark.Engine.Run.RunLog.HumanWidth;
        }
    }

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const int EnableVirtualTerminalProcessing = 0x0004;
    private static readonly Dictionary<int, int> SavedModes = [];

    private static bool EnableVirtualTerminal(int handle)
    {
        try
        {
            var standard = GetStdHandle(handle);
            if (standard == IntPtr.Zero || standard == new IntPtr(-1)) return false;
            if (!GetConsoleMode(standard, out var mode)) return false;
            if (!SetConsoleMode(standard, mode | EnableVirtualTerminalProcessing)) return false;
            SavedModes[handle] = mode;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RestoreConsoleMode()
    {
        foreach (var (handle, mode) in SavedModes)
            try { SetConsoleMode(GetStdHandle(handle), mode); } catch (Exception) { }
        SavedModes.Clear();
    }

    // Each stream is probed and enabled independently: one may be redirected while the other is a console.
    // A console stream is always re-wrapped to its own width; colour is added only when VT is available.
    private static TextWriter MaybeHuman(TextWriter writer, bool output, int width)
    {
        if (!OperatingSystem.IsWindows() || ReferenceEquals(writer, TextWriter.Null)) return writer;
        try { if (output ? Console.IsOutputRedirected : Console.IsErrorRedirected) return writer; }
        catch (Exception) { return writer; }
        var colour = Environment.GetEnvironmentVariable("NO_COLOR") is null &&
            EnableVirtualTerminal(output ? StdOutputHandle : StdErrorHandle);
        return new HumanTextWriter(writer, width, colour);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr handle, out int mode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr handle, int mode);

    // Labels carry the meaning; colour is secondary. Only a real attached console receives these sequences, and
    // wrapping happens with or without it. The labels come from the strings owner rather than being restated here.
    private sealed class HumanTextWriter(TextWriter inner, int width, bool colour) : TextWriter
    {
        private const int PartialFlushLength = 4096;
        private const string Amber = "\u001b[33m";
        private const string Cyan = "\u001b[36m";
        private const string Red = "\u001b[31m";
        private const string Green = "\u001b[32m";
        private const string BoldCyan = "\u001b[1;36m";
        private const string Reset = "\u001b[0m";
        private static readonly string AttentionLabel = Text.Get(TextKey.HumanAttentionFileNotSynced);
        private static readonly string NoticeLabel = Text.Get(TextKey.HumanNotice);
        private static readonly string WaitingLabel = Text.Get(TextKey.HumanWaitLabel);
        private static readonly string StoppedLabel = Text.Get(TextKey.HumanStopped);
        private static readonly string ErrorLabel = Text.Get(TextKey.HumanError);
        private static readonly string OkLabel = Text.Get(TextKey.HumanOkLabel);
        private readonly System.Text.StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = _line.ToString();
                _line.Clear();
                if (line.EndsWith('\r')) line = line[..^1];
                Emit(line);
            }
            else
            {
                _line.Append(value);
                if (_line.Length >= PartialFlushLength)
                {
                    Emit(_line.ToString());
                    _line.Clear();
                }
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var character in value) Write(character);
        }

        public override void WriteLine(string? value) => Write(value + Environment.NewLine);

        public override void WriteLine() => Write(Environment.NewLine);

        public override Task WriteAsync(string? value) { Write(value); return Task.CompletedTask; }

        public override Task WriteLineAsync(string? value) { WriteLine(value); return Task.CompletedTask; }

        public override Task WriteLineAsync() { WriteLine(); return Task.CompletedTask; }

        public override void Flush()
        {
            if (_line.Length > 0)
            {
                Emit(_line.ToString());
                _line.Clear();
            }
            inner.Flush();
        }

        public override Task FlushAsync() { Flush(); return Task.CompletedTask; }

        // A hosted command was wrapped by the host's default width; the client re-wraps to its own terminal.
        private void Emit(string line)
        {
            foreach (var part in Wrap(line))
            {
                inner.Write(colour ? Decorate(part) : part);
                inner.Write(Environment.NewLine);
            }
        }

        private IEnumerable<string> Wrap(string line)
        {
            if (width <= 0 || line.Length <= width)
            {
                yield return line;
                yield break;
            }
            var remaining = line;
            while (remaining.Length > width)
            {
                var take = width;
                if (char.IsHighSurrogate(remaining[take - 1])) take--;
                yield return remaining[..take];
                remaining = remaining[take..];
            }
            yield return remaining;
        }

        private static string Decorate(string line)
        {
            var colour = line.Contains(AttentionLabel, StringComparison.Ordinal) ? Amber
                : line.Contains(WaitingLabel, StringComparison.Ordinal) ? Amber
                : line.Contains(NoticeLabel, StringComparison.Ordinal) ? Cyan
                : line.Contains(StoppedLabel, StringComparison.Ordinal) ? Amber
                : line.Contains(ErrorLabel, StringComparison.Ordinal) ? Red
                : line.Contains(OkLabel, StringComparison.Ordinal) ? Green
                : IsHeading(line) ? BoldCyan : null;
            return colour is null ? line : colour + line + Reset;
        }

        private static bool IsHeading(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return false;
            var letter = false;
            foreach (var character in trimmed)
            {
                if (!char.IsLetter(character)) continue;
                letter = true;
                if (char.IsLower(character)) return false;
            }
            return letter;
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
