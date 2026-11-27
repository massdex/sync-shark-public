using System.Runtime.InteropServices;
using System.Text;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Colours each whole line on an attached console by the label it carries: attention, waits and stops amber,
/// notices cyan, errors red, done green. The label still carries the meaning; a redirected stream, NO_COLOR or a console
/// without VT processing keeps the plain writer.</summary>
public sealed class ColourWriter(TextWriter inner) : TextWriter
{
    // ECMA-48 SGR foreground colours and the reset.
    private const int Amber = 33, Red = 31, Green = 32, Cyan = 36, Reset = 0;
    // winbase.h STD_OUTPUT_HANDLE and STD_ERROR_HANDLE; wincon.h ENABLE_VIRTUAL_TERMINAL_PROCESSING.
    private const int OutputHandle = -11, ErrorHandle = -12, VirtualTerminal = 0x0004;
    private static readonly (TextKey Label, int Colour)[] Labels =
    [
        (TextKey.HumanAttention, Amber), (TextKey.HumanWaitLabel, Amber), (TextKey.HumanStopped, Amber),
        (TextKey.HumanNotice, Cyan), (TextKey.HumanError, Red), (TextKey.HumanOkLabel, Green), (TextKey.HumanFinishedComplete, Green),
    ];
    private readonly StringBuilder _line = new();

    /// <summary>The console writer to use: coloured only when the stream is an attached console, NO_COLOR is unset and VT
    /// processing can be enabled.</summary>
    internal static TextWriter For(TextWriter writer, bool error)
    {
        if ((error ? Console.IsErrorRedirected : Console.IsOutputRedirected) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ResourceNames.NoColorVariable)) ||
            !EnableVirtualTerminal(error ? ErrorHandle : OutputHandle)) return writer;
        return Synchronized(new ColourWriter(writer));
    }

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        if (value != '\n') { _line.Append(value); return; }
        if (_line.Length != 0 && _line[^1] == '\r') _line.Length--;
        inner.Write(Decorate(_line.ToString()));
        inner.WriteLine();
        _line.Clear();
    }

    /// <summary>Writes a final unterminated line as it is.</summary>
    public override void Flush()
    {
        if (_line.Length != 0) { inner.Write(Decorate(_line.ToString())); _line.Clear(); }
        inner.Flush();
    }

    private static string Decorate(string line)
    {
        foreach (var (label, colour) in Labels)
            if (line.Contains(Text.Get(label), StringComparison.Ordinal)) return Sgr(colour) + line + Sgr(Reset);
        return line;
    }

    private static string Sgr(int code) => "\u001b[" + code + "m";

    // shortcut: VT processing stays on after exit (modern consoles keep it on anyway); restore the saved mode if a console shows stray codes.
    private static bool EnableVirtualTerminal(int which)
    {
        var handle = GetStdHandle(which);
        return GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | VirtualTerminal);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint handle, out int mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint handle, int mode);
}
