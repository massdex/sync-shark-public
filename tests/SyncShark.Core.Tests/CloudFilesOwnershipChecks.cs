using System.Buffers.Binary;
using Massdex.SyncShark;

internal static class CloudFilesOwnershipChecks
{
    internal static void Run()
    {
        const string path = "X:/Parent/Child";
        const string token = "49ad020a-fca3-4c18-88e7-9e03abe27cb1";
        var identity = WindowsCloudFileIdentity.Root("pair-a") with { V2OwnershipToken = token };
        CloudFilesRootRegistration? native = Decode(identity.Serialize());
        var reads = 0;
        CloudFilesRootRegistration? Read(string _) { reads++; return native; }
        bool Owns(string candidate, string id) => candidate == path && id == token;
        string? Inspect(string candidate = path) =>
            WindowsCloudFilesProvider.RequireRecordedRegistration(candidate, Read, Owns);

        Require(Inspect() == token && reads == 1, "owned GUID works without any Explorer registry entry");
        Reject(() => Inspect("X:/Parent/Other"), "GUID at a different path is foreign");
        native = Decode((identity with { V2OwnershipToken = "66d833c0-a182-4c51-ab29-6b7179f60372" }).Serialize());
        Reject(() => Inspect(), "replacement registration with same provider and file id is foreign");
        native = Decode(WindowsCloudFileIdentity.Root("pair-a").Serialize());
        Reject(() => Inspect(), "legacy same-provider same-pair identity is not v2 ownership");
        native = Decode(System.Text.Encoding.UTF8.GetBytes("foreign-provider-identity"));
        Reject(() => Inspect(), "opaque foreign identity is foreign");
        native = null;
        Require(Inspect() is null, "missing registration is not owned and allows explicit creation only");
        Reject(() => WindowsCloudFilesProvider.RequireRecordedRegistration(path,
            _ => throw new IOException("native read failed"), Owns), "native read failure fails closed");
        Reject(() => WindowsCloudFilesProvider.ParseRootRegistration(new byte[1056], 1055),
            "truncated standard info fails closed");
        var malformed = new byte[1056];
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(1052), 4096);
        Reject(() => WindowsCloudFilesProvider.ParseRootRegistration(malformed, malformed.Length),
            "identity length beyond returned bytes fails closed");
    }

    private static CloudFilesRootRegistration Decode(byte[] identity)
    {
        var bytes = new byte[1056 + identity.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1052), (uint)identity.Length);
        identity.CopyTo(bytes, 1056);
        return WindowsCloudFilesProvider.ParseRootRegistration(bytes, bytes.Length);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("Cloud Files ownership: " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Cloud Files ownership: " + message);
    }
}
