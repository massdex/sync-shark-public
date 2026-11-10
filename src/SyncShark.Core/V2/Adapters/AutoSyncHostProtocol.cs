using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Versioned, store-scoped local protocol. This is a transport boundary, not an alternative state authority.
/// Production must supply the existing product-lock lease before constructing an engine.
/// </summary>
public static class AutoSyncHostProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>No state is opened or created when deriving the address.</summary>
    public static string PipeName(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        if (!Path.IsPathFullyQualified(lockPath)) throw new ArgumentException("An absolute authority lock path is required.", nameof(lockPath));
        var path = Path.GetFullPath(lockPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        // The same store has one authority per user/session, matching the existing GUI bridge's peer boundary.
        var scope = path + "\n" + Environment.UserDomainName + "\\" + Environment.UserName + "\n" +
            Process.GetCurrentProcess().SessionId;
        return "SyncShark.AutoSync.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("The host response exceeds the bounded IPC frame; request a smaller page.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaximumFrameBytes) throw new InvalidDataException("Invalid or oversized host frame.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("An empty host frame is not a request.");
    }

}
