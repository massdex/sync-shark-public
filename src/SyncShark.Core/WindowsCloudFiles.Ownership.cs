using System.Buffers.Binary;

namespace Massdex.SyncShark;

internal sealed record CloudFilesRootRegistration(ulong FileId, WindowsCloudFileIdentity? Identity)
{
    internal string? OwnershipToken => Identity?.ObjectKind == "root" &&
        Guid.TryParseExact(Identity.V2OwnershipToken, "D", out var token) && token != Guid.Empty
            ? token.ToString("D") : null;
}

public sealed partial class WindowsCloudFilesProvider
{
    // Set only from the durable v2 record (or by an explicit disposable native fixture).
    internal string? ExpectedRegistrationId { get; set; }

    /// <summary>Reads the native SyncRootIdentity, never Explorer's optional registry publication.
    /// Null means no registration; malformed/foreign identity remains a registration with no ownership token.
    /// Unexpected native failures throw, rather than granting permission to register or reconnect.</summary>
    internal static CloudFilesRootRegistration? ReadRootRegistration(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var data = new byte[8192];
        var result = Native.CfGetSyncRootInfoByPath(Path.GetFullPath(path), 1, data, data.Length, out var returned);
        if (result is unchecked((int)0x80070178) or unchecked((int)0x80070186) or unchecked((int)0x80070195)) return null;
        Check(result, "Reading native sync-root identity");
        return ParseRootRegistration(data, returned);
    }

    internal static CloudFilesRootRegistration ParseRootRegistration(byte[] data, int returned)
    {
        // CF_SYNC_ROOT_STANDARD_INFO: 8-byte id, two 4-byte policies, three DWORDs,
        // two WCHAR[256] strings, ULONG identity length, then the identity bytes.
        const int lengthOffset = 1052;
        const int identityOffset = 1056;
        if (returned < identityOffset || returned > data.Length)
            throw new IOException("Windows returned incomplete sync-root identity information.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(lengthOffset));
        if (length > 4096 || length > returned - identityOffset)
            throw new IOException("Windows returned an invalid sync-root identity length.");
        WindowsCloudFileIdentity? identity = null;
        if (length != 0) WindowsCloudFileIdentity.TryDeserialize(data.AsSpan(identityOffset, (int)length), out identity);
        return new(BinaryPrimitives.ReadUInt64LittleEndian(data), identity);
    }

    internal static string? RequireRecordedRegistration(string path, Func<string, CloudFilesRootRegistration?> read,
        Func<string, string, bool> owns)
    {
        var registration = read(path);
        if (registration is null) return null;
        var token = registration.OwnershipToken;
        if (token is null || !owns(path, token))
            throw new IOException("The native sync-root identity and path do not match a recorded v2 registration. " +
                "Nothing was connected or changed; keep files offline, review the registration, or choose a separate root.");
        return token;
    }
}
