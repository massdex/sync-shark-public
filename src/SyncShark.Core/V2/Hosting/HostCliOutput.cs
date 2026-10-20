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
            if (value.Length >= maximumCharacters)
            {
                Truncated |= _text.Length > 0 || value.Length > maximumCharacters;
                _text.Clear();
                _text.Append(value.AsSpan(value.Length - maximumCharacters));
            }
            else
            {
                var overflow = _text.Length + value.Length - maximumCharacters;
                if (overflow > 0) { Truncated = true; _text.Remove(0, overflow); }
                _text.Append(value);
            }
        }
    }
    public override void WriteLine(string? value) { Write(value); Write(NewLine); }
    public override string ToString() { lock (_gate) return _text.ToString(); }
}
