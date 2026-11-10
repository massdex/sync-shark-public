using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Incremental legacy prepare reader. JsonSerializer stream converters read ahead over an entire converted array;
/// this reader instead buffers only one entry, so migrating a million matches never buffers the full document.
/// </summary>
internal sealed class PrepareJsonReader(Stream stream)
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _start, _end;
    private bool _final;
    private JsonReaderState _state;
    private bool _firstFill = true;

    public (JsonTokenType Token, string? Text) Token()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), _final, _state);
            if (reader.Read())
            {
                var token = reader.TokenType;
                var text = token == JsonTokenType.PropertyName ? reader.GetString() : null;
                Consume(ref reader);
                return (token, text);
            }
            if (_final) return (JsonTokenType.None, null);
            Fill();
        }
    }

    /// <summary>Reads one value; null represents the closing array token, never a JSON null value.</summary>
    public JsonDocument? ValueOrArrayEnd()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), _final, _state);
            if (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    Consume(ref reader);
                    return null;
                }
                if (JsonDocument.TryParseValue(ref reader, out var value))
                {
                    Consume(ref reader);
                    return value;
                }
            }
            if (_final) throw new JsonException("Incomplete prepare value.");
            Fill();
        }
    }

    private void Consume(ref Utf8JsonReader reader)
    {
        _start += checked((int)reader.BytesConsumed);
        _state = reader.CurrentState;
    }

    private void Fill()
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) Array.Resize(ref _buffer, checked(_buffer.Length * 2));
        do
        {
            var count = stream.Read(_buffer, _end, _buffer.Length - _end);
            _final = count == 0;
            _end += count;
        } while (_firstFill && _end < 3 && !_final);
        // Older File.WriteAllText callers may have emitted a UTF-8 BOM. Stream.Read may return a single byte.
        if (_firstFill)
        {
            if (_end >= 3 && _buffer[0] == 0xef && _buffer[1] == 0xbb && _buffer[2] == 0xbf) _start = 3;
            _firstFill = false;
        }
    }
}
