using System.Reflection;

namespace Massdex.SyncShark;

public static class BuildIdentity
{
    public static string InformationalVersion => typeof(BuildIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string SourceRevision => typeof(BuildIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(item => item.Key == "SyncSharkSourceRevision")?.Value ?? "UNBOUND";

    public static void RequireExactRevision(string expectedRevision)
    {
        if (expectedRevision.Length != 40 || !expectedRevision.All(Uri.IsHexDigit) ||
            !SourceRevision.Equals(expectedRevision, StringComparison.OrdinalIgnoreCase) ||
            !InformationalVersion.EndsWith("+" + expectedRevision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The running build is not bound to the expected frozen source revision.");
    }
}
