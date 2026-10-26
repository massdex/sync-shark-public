using System.Text;

namespace Massdex.SyncShark.V2;

internal sealed class HostCliWriter(int maximumCharacters) : TextWriter
{
    private char[] _buffer = new char[Math.Min(256, maximumCharacters)];
    private int _next, _count;
    private readonly object _gate = new();
    public override Encoding Encoding => Encoding.UTF8;
    public bool Truncated { get; private set; }
    public override void Write(char value)
    {
        lock (_gate) Append(value);
    }
    private void Append(char value)
    {
        if (_count == _buffer.Length && _buffer.Length < maximumCharacters)
        {
            Array.Resize(ref _buffer, Math.Min(maximumCharacters, _buffer.Length * 2));
            _next = _count; // no wrap until the maximum is reached
        }
        if (_count == _buffer.Length) Truncated = true;
        else _count++;
        _buffer[_next] = value;
        _next = (_next + 1) % _buffer.Length;
    }
    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_gate)
        {
            var start = Math.Max(0, value.Length - maximumCharacters);
            if (start > 0) Truncated = true;
            for (var i = start; i < value.Length; i++) Append(value[i]);
        }
    }
    public override void WriteLine(string? value) { Write(value); Write(NewLine); }
    public override string ToString()
    {
        lock (_gate)
        {
            var start = (_next - _count + _buffer.Length) % _buffer.Length;
            var first = Math.Min(_count, _buffer.Length - start);
            return new string(_buffer, start, first) + new string(_buffer, 0, _count - first);
        }
    }
}
