using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

internal sealed class DualSlotJsonStore<T>
{
    internal const int MaximumBytes = 1024 * 1024;
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new();
    private readonly PilotStateAuthority _authority;
    private readonly string _slotA;
    private readonly string _slotB;
    private readonly object _gate;

    public DualSlotJsonStore(PilotStateAuthority authority, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName) || Path.GetFileName(baseName) != baseName || baseName.Contains('.'))
            throw new ArgumentException("Dual-slot state base name is not accepted.", nameof(baseName));
        _authority = authority;
        _gate = authority.StateStoreGate(baseName);
        _slotA = baseName + "-a.json";
        _slotB = baseName + "-b.json";
    }

    public T? Load()
    {
        lock (_gate)
        {
            var a = ReadSlot(_slotA);
            var b = ReadSlot(_slotB);
            var selected = SelectCurrent(a, b);
            if (selected is not null) return selected.Value;
            if (a.Exists || b.Exists) throw new InvalidDataException("Both alternating state slots are invalid; neither was overwritten.");
            return default;
        }
    }

    public T LoadOrCreate(Func<T> create)
    {
        lock (_gate)
        {
            var a = ReadSlot(_slotA);
            var b = ReadSlot(_slotB);
            var selected = SelectCurrent(a, b);
            if (selected is not null) return selected.Value!;
            if (a.Exists || b.Exists)
                throw new InvalidDataException("No valid alternating state slot is available. Existing state was preserved; restore a verified state backup before retrying.");

            var initial = create() ?? throw new InvalidDataException("Initial alternating state cannot be null.");
            WriteSlot(a.Exists ? _slotB : _slotA, initial, 1);
            return initial;
        }
    }

    public Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = ReadSlot(_slotA);
            var b = ReadSlot(_slotB);
            var current = SelectCurrent(a, b);
            if (current is null && (a.Exists || b.Exists))
                throw new InvalidDataException("Both alternating state slots are invalid; save stopped without overwriting either.");
            var generation = checked((current?.Generation ?? 0) + 1);
            var target = !a.Valid ? _slotA : !b.Valid ? _slotB : a.Generation <= b.Generation ? _slotA : _slotB;
            WriteSlot(target, value, generation);
            return Task.CompletedTask;
        }
    }

    private void WriteSlot(string target, T value, long generation)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        var record = new SlotRecord<T>(SchemaVersion, generation, value,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Alternating state exceeds its 1 MiB slot ceiling.");

        using var stream = _authority.OpenStateFile(target, create: true, write: true) ??
                           throw new IOException("The alternating state slot was not created.");
        _authority.VerifyStateFile(stream, target);
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(true);
        _authority.VerifyStateFile(stream, target);
        stream.Position = 0;
        var committed = new byte[checked((int)stream.Length)];
        stream.ReadExactly(committed);
        var verified = Parse(target, committed, exists: true);
        if (!verified.Valid || verified.Generation != generation)
            throw new IOException("Alternating state slot readback did not verify.");
        _authority.VerifyStateFile(stream, target);
    }

    private SlotState ReadSlot(string fileName)
    {
        using var stream = _authority.OpenStateFile(fileName, create: false, write: false);
        if (stream is null) return new SlotState(false, false, 0, default);
        _authority.VerifyStateFile(stream, fileName);
        if (stream.Length is <= 0 or > MaximumBytes) return new SlotState(true, false, 0, default);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        _authority.VerifyStateFile(stream, fileName);
        return Parse(fileName, bytes, exists: true);
    }

    private static SlotState Parse(string fileName, byte[] bytes, bool exists)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var record = JsonSerializer.Deserialize<SlotRecord<T>>(bytes, Options);
            if (record is null || record.SchemaVersion != SchemaVersion || record.Generation <= 0 || record.Value is null)
                return new SlotState(exists, false, 0, default);
            if (!document.RootElement.TryGetProperty(nameof(SlotRecord<T>.Value), out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new SlotState(exists, false, 0, default);
            var claimed = Convert.FromHexString(record.PayloadSha256);
            var payload = Encoding.UTF8.GetBytes(value.GetRawText());
            if (!CryptographicOperations.FixedTimeEquals(claimed, SHA256.HashData(payload)))
                return new SlotState(exists, false, 0, default);
            return new SlotState(exists, true, record.Generation, record.Value);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            _ = fileName;
            return new SlotState(exists, false, 0, default);
        }
    }

    private static SlotState? SelectCurrent(SlotState a, SlotState b)
    {
        if (a.Valid && b.Valid) return a.Generation >= b.Generation ? a : b;
        if (a.Valid) return a;
        if (b.Valid) return b;
        return null;
    }

    private sealed record SlotRecord<TValue>(int SchemaVersion, long Generation, TValue Value, string PayloadSha256);
    private sealed record SlotState(bool Exists, bool Valid, long Generation, T? Value);
}
