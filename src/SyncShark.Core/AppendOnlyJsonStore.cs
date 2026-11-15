using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

internal sealed class AppendOnlyJsonStore<T>(PilotStateAuthority authority, string fileName)
{
    private const int SchemaVersion = 1;
    internal const int PhysicalMaximumRecords = 100;
    internal const int PhysicalMaximumBytes = 1024 * 1024;
    internal const int RecoveryRecordReserve = 2;
    internal const int OperationalMaximumRecords = PhysicalMaximumRecords - RecoveryRecordReserve;
    private static readonly JsonSerializerOptions Options = new();

    public bool Exists => authority.StateFileExists(fileName);

    public T? Load()
    {
        using var stream = authority.OpenStateFile(fileName, create: false, write: false);
        if (stream is null) return default;
        authority.VerifyStateFile(stream, fileName);
        var bytes = ReadBounded(stream);
        var analysis = Analyze(bytes);
        authority.VerifyStateFile(stream, fileName);
        return analysis.Current;
    }

    public Task AppendSetAsync(T value, CancellationToken cancellationToken) => AppendAsync("set", value, cancellationToken);

    public Task AppendDeleteAsync(CancellationToken cancellationToken) => AppendAsync("delete", default, cancellationToken);

    private Task AppendAsync(string operation, T? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = authority.OpenStateFile(fileName, create: true, write: true) ?? throw new IOException("The state file was not created.");
        authority.VerifyStateFile(stream, fileName);
        var existing = ReadBounded(stream);
        var analysis = Analyze(existing);
        var operationLine = SerializeOperationLine(operation, value);

        byte[]? recoveryLine = null;
        if (analysis.InvalidSuffix.Length > 0)
        {
            var originalSha = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
            var badSha = Convert.ToHexString(SHA256.HashData(analysis.InvalidSuffix)).ToLowerInvariant();
            var evidence = new RecoveryEvidence(existing.Length, originalSha, analysis.ValidPrefixLength,
                analysis.InvalidSuffix.Length, badSha, Convert.ToBase64String(analysis.InvalidSuffix));
            var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, Options);
            var evidenceName = authority.PersistRecoveryEvidence(fileName, originalSha, evidenceBytes);
            var recovery = new RecoveryState(evidenceName, originalSha, analysis.ValidPrefixLength,
                analysis.InvalidSuffix.Length, badSha, operation == "delete" ? "tombstone" : "set");
            var recoveryPayload = JsonSerializer.SerializeToUtf8Bytes(recovery, Options);
            var recoveryRecord = new AppendRecord<T>(SchemaVersion, "recovery", default,
                Convert.ToHexString(SHA256.HashData(recoveryPayload)).ToLowerInvariant(), recovery);
            recoveryLine = JsonSerializer.SerializeToUtf8Bytes(recoveryRecord, Options);
        }

        var expectedRecordCount = recoveryLine is null ? analysis.RecordCount + 1 : RecoveryRecordReserve;
        var recoveryByteReserve = GetRecoveryByteReserve(fileName, operationLine);
        if (recoveryLine is null)
        {
            if (expectedRecordCount > OperationalMaximumRecords)
                throw new IOException("Append-only operational state would consume its two-record recovery reserve.");
            var operationalLength = checked(analysis.ValidPrefixLength + operationLine.Length + 1);
            if (operationalLength > PhysicalMaximumBytes - recoveryByteReserve)
                throw new IOException("Append-only operational state would consume its derived recovery byte reserve.");
        }
        else
        {
            var recoveryLength = checked(recoveryLine.Length + 1 + operationLine.Length + 1);
            if (expectedRecordCount > PhysicalMaximumRecords || recoveryLength > PhysicalMaximumBytes || recoveryLength > recoveryByteReserve)
                throw new IOException("Append-only recovery emission exceeds its structural reserve.");
        }

        authority.VerifyStateFile(stream, fileName);
        if (recoveryLine is not null)
        {
            var successor = new byte[checked(recoveryLine.Length + 1 + operationLine.Length + 1)];
            recoveryLine.CopyTo(successor, 0);
            successor[recoveryLine.Length] = (byte)'\n';
            operationLine.CopyTo(successor, recoveryLine.Length + 1);
            successor[^1] = (byte)'\n';
            stream.Position = 0;
            stream.Write(successor);
            stream.SetLength(successor.Length);
        }
        else
        {
            stream.Position = analysis.ValidPrefixLength;
            stream.Write(operationLine);
            stream.WriteByte((byte)'\n');
            stream.SetLength(stream.Position);
        }
        stream.Flush(true);
        authority.VerifyStateFile(stream, fileName);
        stream.Position = 0;
        var committed = ReadBounded(stream);
        var committedAnalysis = Analyze(committed);
        if (committedAnalysis.InvalidSuffix.Length != 0 || committedAnalysis.ValidPrefixLength != committed.Length ||
            committedAnalysis.RecordCount != expectedRecordCount)
            throw new IOException("Append-only state readback is not a complete valid successor.");
        authority.VerifyStateFile(stream, fileName);
        return Task.CompletedTask;
    }

    private static byte[] ReadBounded(FileStream stream)
    {
        if (stream.Length > PhysicalMaximumBytes) throw new InvalidDataException("Append-only state exceeds its 1 MiB ceiling.");
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static Analysis Analyze(byte[] bytes)
    {
        T? current = default;
        var start = 0;
        var validPrefixLength = 0;
        var records = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            if (index == start)
            {
                start = index + 1;
                validPrefixLength = start;
                continue;
            }
            var line = bytes.AsSpan(start, index - start);
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!TryApply(line, ref current)) break;
            records++;
            if (records > PhysicalMaximumRecords) throw new InvalidDataException("Append-only state exceeds its 100-record ceiling.");
            start = index + 1;
            validPrefixLength = start;
        }
        var invalid = bytes.AsSpan(validPrefixLength).ToArray();
        return new Analysis(current, validPrefixLength, records, invalid);
    }

    private static bool TryApply(ReadOnlySpan<byte> line, ref T? current)
    {
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var record = JsonSerializer.Deserialize<AppendRecord<T>>(line, Options);
            if (record is null || record.SchemaVersion != SchemaVersion || record.Operation is not ("set" or "delete" or "recovery")) return false;
            byte[] payload;
            switch (record.Operation)
            {
                case "set":
                    if (record.Value is null) return false;
                    if (!document.RootElement.TryGetProperty(nameof(AppendRecord<T>.Value), out var value) ||
                        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        return false;
                    payload = Encoding.UTF8.GetBytes(value.GetRawText());
                    break;
                case "delete":
                    payload = Encoding.UTF8.GetBytes("delete");
                    break;
                default:
                    if (record.Recovery is null || string.IsNullOrWhiteSpace(record.Recovery.EvidenceFile)) return false;
                    if (!document.RootElement.TryGetProperty(nameof(AppendRecord<T>.Recovery), out var recovery) ||
                        recovery.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        return false;
                    payload = Encoding.UTF8.GetBytes(recovery.GetRawText());
                    break;
            }
            var claimed = Convert.FromHexString(record.PayloadSha256);
            if (!CryptographicOperations.FixedTimeEquals(claimed, SHA256.HashData(payload))) return false;
            if (record.Operation == "set") current = record.Value;
            if (record.Operation == "delete") current = default;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    internal static byte[] SerializeSetLine(T value) => SerializeOperationLine("set", value);

    internal static int RecoveryByteReserveForSet(string stateFileName, T value) =>
        GetRecoveryByteReserve(stateFileName, SerializeOperationLine("set", value));

    private static byte[] SerializeOperationLine(string operation, T? value)
    {
        var payload = operation == "set" ? JsonSerializer.SerializeToUtf8Bytes(value, Options) : Encoding.UTF8.GetBytes("delete");
        var operationRecord = new AppendRecord<T>(SchemaVersion, operation, value,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), null);
        return JsonSerializer.SerializeToUtf8Bytes(operationRecord, Options);
    }

    private static int GetRecoveryByteReserve(string stateFileName, byte[] requestedOperationLine)
    {
        var maximumHash = new string('f', 64);
        var maximumRecovery = new RecoveryState(
            stateFileName + ".recovery-" + maximumHash + ".json",
            maximumHash,
            PhysicalMaximumBytes,
            PhysicalMaximumBytes,
            maximumHash,
            "tombstone");
        var recoveryPayload = JsonSerializer.SerializeToUtf8Bytes(maximumRecovery, Options);
        var recoveryRecord = new AppendRecord<T>(SchemaVersion, "recovery", default,
            Convert.ToHexString(SHA256.HashData(recoveryPayload)).ToLowerInvariant(), maximumRecovery);
        var maximumRecoveryLine = JsonSerializer.SerializeToUtf8Bytes(recoveryRecord, Options);
        var deleteLine = SerializeOperationLine("delete", default);
        return checked(maximumRecoveryLine.Length + 1 + Math.Max(requestedOperationLine.Length, deleteLine.Length) + 1);
    }

    private sealed record AppendRecord<TValue>(int SchemaVersion, string Operation, TValue? Value, string PayloadSha256, RecoveryState? Recovery);
    private sealed record RecoveryState(string EvidenceFile, string OriginalSha256, int ValidPrefixByteLength,
        int BadByteLength, string BadSha256, string Outcome);
    private sealed record RecoveryEvidence(int OriginalByteLength, string OriginalSha256, int ValidPrefixByteLength,
        int BadByteLength, string BadSha256, string BadBytesBase64);
    private sealed record Analysis(T? Current, int ValidPrefixLength, int RecordCount, byte[] InvalidSuffix);
}
