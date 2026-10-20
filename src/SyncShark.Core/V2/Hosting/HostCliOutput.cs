using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Preserves the existing CLI envelope and exit code across the shared host boundary.</summary>
public sealed record HostCliOutput(int ExitCode, string Stdout, string Stderr);

internal sealed class HostCliWriter(int maximumCharacters) : TextWriter
{
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();
    public override Encoding Encoding => Encoding.UTF8;
    public bool Truncated { get; private set; }
    public override void Write(char value) => Write(value.ToString());
    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_gate)
        {
            var available = maximumCharacters - _text.Length;
            if (value.Length > available) Truncated = true;
            if (available > 0) _text.Append(value.AsSpan(0, Math.Min(value.Length, available)));
        }
    }
    public override void WriteLine(string? value) { Write(value); Write(NewLine); }
    public override string ToString() { lock (_gate) return _text.ToString(); }
}
