using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Validates the original reply token stream without retaining unselected scalars.</summary>
internal sealed partial class ReplyTokens : IDisposable
{
    // The no-field index: no known name matched, or no selected field is pending (shared with DropboxEntries.Projection).
    internal const int NoField = -1;
    // Unicode/UTF-8 encoding widths and ranges, not reply-size or depth policy.
    private const int HexLetterOffset = 10;
    private const int HexBits = 4;
    private const int EscapeDigits = 4;
    private const int HighSurrogateStart = 0xd800;
    private const int HighSurrogateEnd = 0xdbff;
    private const int LowSurrogateStart = 0xdc00;
    private const int LowSurrogateEnd = 0xdfff;
    private const int SurrogateBits = 10;
    private const int SupplementaryStart = 0x10000;
    private const int UnicodeMaximum = 0x10ffff;
    private const int ControlLimit = 0x20;
    private const int AsciiLimit = 0x80;
    private const int ContinuationMask = 0xc0;
    private const int ContinuationTag = 0x80;
    private const int ContinuationBits = 6;
    private const int ContinuationPayload = 0x3f;
    private const int TwoByteStart = 0xc2;
    private const int TwoByteEnd = 0xdf;
    private const int TwoBytePayload = 0x1f;
    private const int TwoByteRemaining = 1;
    private const int ThreeByteStart = 0xe0;
    private const int ThreeByteEnd = 0xef;
    private const int ThreeBytePayload = 0x0f;
    private const int ThreeByteRemaining = 2;
    private const int ThreeByteMinimum = 0x800;
    private const int FourByteStart = 0xf0;
    private const int FourByteEnd = 0xf4;
    private const int FourBytePayload = 0x07;
    private const int FourByteRemaining = 3;
    private const int BitsPerByte = 8;
    private const int NameCapacity = sizeof(ulong) * BitsPerByte;
    private const ulong NameBit = 1UL;
    private const int LiteralPrefixLength = 1;

    private static ReadOnlySpan<byte> TrueToken => "true"u8;
    private static ReadOnlySpan<byte> FalseToken => "false"u8;
    private static ReadOnlySpan<byte> NullToken => "null"u8;
    private static ReadOnlySpan<byte> EmptyStringToken => "\"\""u8;
    // Surrogates validate syntax without retaining the actual property or number text.
    private static ReadOnlySpan<byte> PropertyToken => "\"p\":"u8;
    private static ReadOnlySpan<byte> NumberToken => "0 "u8;

    internal partial (int Consumed, bool TokenReady) Read(
        ReadOnlySpan<byte> bytes, bool isFinalBlock, ReadOnlySpan<string> knownNames,
        bool retainScalar, CancellationToken ct);

    internal JsonTokenType TokenType { get; private set; }

    internal int KnownNameIndex { get; private set; } = NoField;

    internal partial byte[]? TakeScalar();

    internal bool IsComplete { get; private set; }

    internal partial void RequireEnd();

    public partial void Dispose();

    private readonly List<Frame> _frames = [];
    private JsonReaderState _syntax;
    private MemoryStream? _scalar;
    private byte[]? _value;
    private Lexeme _lexeme;
    private int _namePosition;
    private ulong _nameCandidates;
    private bool _property;
    private bool _escaped;
    private int _unicodeDigits;
    private int _unicodeValue;
    private int _highSurrogate;
    private int _utf8Remaining;
    private int _utf8Value;
    private int _utf8Minimum;
    private NumberPosition _numberState;
    private int _literalPosition;
    private byte _literalStart;
    private bool _comma;
    private bool _ready;
    private bool _disposed;

    private enum Lexeme { None, String, Number, Literal }
    private enum NumberPosition { Start, Sign, Zero, Integer, Point, Fraction, Exponent, ExponentSign, ExponentDigits }
    private enum Position { PropertyOrEnd, Property, Colon, Value, ValueOrEnd, CommaOrEnd }
    private sealed class Frame(bool objectValue, Position position)
    {
        internal readonly bool ObjectValue = objectValue;
        internal Position Position = position;
    }

    // One token pump advances caller bytes, lexical state and token readiness together, including completing a pending number at EOF without consuming the next byte.
    // The extracted lexer validates original tokens; the platform reader owns grammar and default depth.
    internal partial (int Consumed, bool TokenReady) Read(
        ReadOnlySpan<byte> bytes, bool isFinalBlock, ReadOnlySpan<string> knownNames,
        bool retainScalar, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        _ready = false;
        _value = null;
        KnownNameIndex = NoField;
        var consumed = 0;
        while (consumed < bytes.Length && !_ready)
        {
            ct.ThrowIfCancellationRequested();
            var value = bytes[consumed];
            if (_lexeme == Lexeme.Number && !NumberByte(value))
            {
                EndNumber();
                continue;
            }
            consumed++;
            if (_lexeme != Lexeme.None)
            {
                _scalar?.WriteByte(value);
                switch (_lexeme)
                {
                    case Lexeme.String: StringByte(value, knownNames); break;
                    case Lexeme.Number: AdvanceNumber(value); break;
                    case Lexeme.Literal: LiteralByte(value); break;
                }
                continue;
            }
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            if (IsComplete) throw new JsonException();
            StructuralByte(value, knownNames, retainScalar);
        }
        if (isFinalBlock && !_ready)
        {
            if (_lexeme == Lexeme.Number) EndNumber();
            if (!_ready) RequireEnd();
        }
        return (consumed, _ready);
    }

    // One structural-byte transition decides punctuation, frame closure and lexeme start against the same container position and updates the stack and token state together.
    private void StructuralByte(byte value, ReadOnlySpan<string> knownNames, bool retainScalar)
    {
        var frame = _frames.Count == 0 ? null : _frames[^1];
        if (frame?.Position == Position.Colon)
        {
            if (value != ':') throw new JsonException();
            ValidateToken(PropertyToken);
            frame.Position = Position.Value;
            return;
        }
        if (value is (byte)'}' or (byte)']')
        {
            if (frame is null || frame.ObjectValue != (value == '}') ||
                frame.Position is not (Position.PropertyOrEnd or Position.ValueOrEnd or Position.CommaOrEnd))
                throw new JsonException();
            ValidateToken(stackalloc byte[] { value });
            _frames.RemoveAt(_frames.Count - 1);
            EndValue(value == '}' ? JsonTokenType.EndObject : JsonTokenType.EndArray);
            return;
        }
        if (frame?.Position == Position.CommaOrEnd)
        {
            if (value != ',') throw new JsonException();
            _comma = true;
            frame.Position = frame.ObjectValue ? Position.Property : Position.Value;
            return;
        }
        _property = frame?.Position is Position.PropertyOrEnd or Position.Property;
        if (_property && value != '"') throw new JsonException();
        if (!_property && frame is not null && frame.Position is not (Position.Value or Position.ValueOrEnd))
            throw new JsonException();
        if (!_property && retainScalar && value is not ((byte)'{' or (byte)'['))
        {
            _scalar = new MemoryStream();
            _scalar.WriteByte(value);
        }
        switch (value)
        {
            case (byte)'{':
            case (byte)'[':
                ValidateToken(stackalloc byte[] { value });
                _frames.Add(new Frame(value == '{', value == '{' ? Position.PropertyOrEnd : Position.ValueOrEnd));
                Emit(value == '{' ? JsonTokenType.StartObject : JsonTokenType.StartArray);
                break;
            case (byte)'"':
                _lexeme = Lexeme.String;
                _namePosition = 0;
                if (knownNames.Length > NameCapacity) throw new ArgumentOutOfRangeException(nameof(knownNames));
                _nameCandidates = _property && !knownNames.IsEmpty
                    ? ulong.MaxValue >> (NameCapacity - knownNames.Length) : 0UL;
                break;
            case (byte)'t':
            case (byte)'f':
            case (byte)'n':
                _lexeme = Lexeme.Literal;
                _literalStart = value;
                _literalPosition = LiteralPrefixLength;
                break;
            default:
                if (value != '-' && (value < '0' || value > '9')) throw new JsonException();
                _lexeme = Lexeme.Number;
                _numberState = NumberPosition.Start;
                AdvanceNumber(value);
                break;
        }
    }

    private void ValidateToken(ReadOnlySpan<byte> token)
    {
        // Longest grammar literal, optional comma, and terminating whitespace.
        Span<byte> input = stackalloc byte[FalseToken.Length + 1 + 1];
        var count = 0;
        if (_comma) input[count++] = (byte)',';
        token.CopyTo(input[count..]);
        count += token.Length;
        input[count++] = (byte)' ';
        var reader = new Utf8JsonReader(input[..count], false, _syntax);
        if (!reader.Read()) throw new JsonException();
        _syntax = reader.CurrentState;
        _comma = false;
    }

    // One JSON string-byte transition: escape digits, surrogate pairing and UTF-8 continuations are mutually exclusive states for the same character position and share name matching and completion.
    private void StringByte(byte value, ReadOnlySpan<string> knownNames)
    {
        if (_unicodeDigits != 0)
        {
            var digit = value is >= (byte)'0' and <= (byte)'9' ? value - '0' :
                value is >= (byte)'a' and <= (byte)'f' ? value - 'a' + HexLetterOffset :
                value is >= (byte)'A' and <= (byte)'F' ? value - 'A' + HexLetterOffset : NoField;
            if (digit < 0) throw new JsonException();
            _unicodeValue = (_unicodeValue << HexBits) | digit;
            if (--_unicodeDigits == 0)
            {
                if (_highSurrogate != 0)
                {
                    if (_unicodeValue is < LowSurrogateStart or > LowSurrogateEnd) throw new JsonException();
                    NameCharacter(SupplementaryStart + ((_highSurrogate - HighSurrogateStart) << SurrogateBits) +
                        _unicodeValue - LowSurrogateStart, knownNames);
                    _highSurrogate = 0;
                }
                else if (_unicodeValue is >= HighSurrogateStart and <= HighSurrogateEnd) _highSurrogate = _unicodeValue;
                else
                {
                    if (_unicodeValue is >= LowSurrogateStart and <= LowSurrogateEnd) throw new JsonException();
                    NameCharacter(_unicodeValue, knownNames);
                }
            }
            return;
        }
        if (_escaped)
        {
            _escaped = false;
            if (value == 'u')
            {
                _unicodeDigits = EscapeDigits;
                _unicodeValue = 0;
                return;
            }
            if (_highSurrogate != 0) throw new JsonException();
            NameCharacter(value switch
            {
                // The protocol's one solidus character; this is the JSON \/ escape, not a path separator.
                (byte)'/' => '/',
                (byte)'"' => '"', (byte)'\\' => '\\',
                (byte)'b' => '\b', (byte)'f' => '\f', (byte)'n' => '\n',
                (byte)'r' => '\r', (byte)'t' => '\t', _ => throw new JsonException()
            }, knownNames);
            return;
        }
        if (_utf8Remaining != 0)
        {
            if ((value & ContinuationMask) != ContinuationTag) throw new JsonException();
            _utf8Value = (_utf8Value << ContinuationBits) | (value & ContinuationPayload);
            if (--_utf8Remaining == 0)
            {
                if (_utf8Value < _utf8Minimum || _utf8Value > UnicodeMaximum ||
                    _utf8Value is >= HighSurrogateStart and <= LowSurrogateEnd) throw new JsonException();
                NameCharacter(_utf8Value, knownNames);
            }
            return;
        }
        if (_highSurrogate != 0 && value != '\\') throw new JsonException();
        if (value == '\\')
        {
            _escaped = true;
            return;
        }
        if (value == '"')
        {
            _lexeme = Lexeme.None;
            if (_property)
            {
                for (var i = 0; i < knownNames.Length; i++)
                    if ((_nameCandidates & (NameBit << i)) != 0 && knownNames[i].Length == _namePosition)
                        KnownNameIndex = i;
                _frames[^1].Position = Position.Colon;
                Emit(JsonTokenType.PropertyName);
            }
            else
            {
                ValidateToken(EmptyStringToken);
                EndValue(JsonTokenType.String);
            }
            return;
        }
        if (value < ControlLimit) throw new JsonException();
        if (value < AsciiLimit) NameCharacter(value, knownNames);
        else if (value is >= TwoByteStart and <= TwoByteEnd) StartUtf8(value & TwoBytePayload, TwoByteRemaining, AsciiLimit);
        else if (value is >= ThreeByteStart and <= ThreeByteEnd)
            StartUtf8(value & ThreeBytePayload, ThreeByteRemaining, ThreeByteMinimum);
        else if (value is >= FourByteStart and <= FourByteEnd)
            StartUtf8(value & FourBytePayload, FourByteRemaining, SupplementaryStart);
        else throw new JsonException();
    }

    private void StartUtf8(int value, int remaining, int minimum)
    {
        _utf8Value = value;
        _utf8Remaining = remaining;
        _utf8Minimum = minimum;
    }

    private void NameCharacter(int value, ReadOnlySpan<string> knownNames)
    {
        if (_nameCandidates == 0) return;
        for (var i = 0; i < knownNames.Length; i++)
            if ((_nameCandidates & (NameBit << i)) != 0 &&
                (_namePosition >= knownNames[i].Length || knownNames[i][_namePosition] != value))
                _nameCandidates &= ~(NameBit << i);
        _namePosition++;
    }

    private static bool NumberByte(byte value) => value is
        >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E';

    private void AdvanceNumber(byte value)
    {
        var digit = value is >= (byte)'0' and <= (byte)'9';
        _numberState = _numberState switch
        {
            NumberPosition.Start when value == '-' => NumberPosition.Sign,
            NumberPosition.Start or NumberPosition.Sign when value == '0' => NumberPosition.Zero,
            NumberPosition.Start or NumberPosition.Sign when value is >= (byte)'1' and <= (byte)'9' => NumberPosition.Integer,
            NumberPosition.Integer when digit => NumberPosition.Integer,
            NumberPosition.Zero or NumberPosition.Integer when value == '.' => NumberPosition.Point,
            NumberPosition.Point or NumberPosition.Fraction when digit => NumberPosition.Fraction,
            NumberPosition.Zero or NumberPosition.Integer or NumberPosition.Fraction
                when value is (byte)'e' or (byte)'E' => NumberPosition.Exponent,
            NumberPosition.Exponent when value is (byte)'+' or (byte)'-' => NumberPosition.ExponentSign,
            NumberPosition.Exponent or NumberPosition.ExponentSign or NumberPosition.ExponentDigits
                when digit => NumberPosition.ExponentDigits,
            _ => throw new JsonException()
        };
    }

    private void EndNumber()
    {
        if (_numberState is not (NumberPosition.Zero or NumberPosition.Integer or
            NumberPosition.Fraction or NumberPosition.ExponentDigits)) throw new JsonException();
        _lexeme = Lexeme.None;
        ValidateToken(NumberToken);
        EndValue(JsonTokenType.Number);
    }

    private void LiteralByte(byte value)
    {
        var expected = _literalStart switch
        {
            (byte)'t' => TrueToken, (byte)'f' => FalseToken, _ => NullToken
        };
        if (value != expected[_literalPosition++]) throw new JsonException();
        if (_literalPosition != expected.Length) return;
        _lexeme = Lexeme.None;
        ValidateToken(expected);
        EndValue(_literalStart switch
        {
            (byte)'t' => JsonTokenType.True, (byte)'f' => JsonTokenType.False, _ => JsonTokenType.Null
        });
    }

    private void EndValue(JsonTokenType token)
    {
        if (_scalar is not null)
        {
            _value = _scalar.ToArray();
            _scalar.Dispose();
            _scalar = null;
        }
        if (_frames.Count == 0) IsComplete = true;
        else _frames[^1].Position = Position.CommaOrEnd;
        Emit(token);
    }

    private void Emit(JsonTokenType token)
    {
        TokenType = token;
        _ready = true;
    }

    internal partial byte[]? TakeScalar()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var value = _value;
        _value = null;
        return value;
    }

    internal partial void RequireEnd()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsComplete || _lexeme != Lexeme.None) throw new JsonException();
        var reader = new Utf8JsonReader(ReadOnlySpan<byte>.Empty, true, _syntax);
        if (reader.Read()) throw new JsonException();
    }

    public partial void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scalar?.Dispose();
        _scalar = null;
        _value = null;
        _frames.Clear();
    }
}
