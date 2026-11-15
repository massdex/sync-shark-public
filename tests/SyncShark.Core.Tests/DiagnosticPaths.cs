using System;
using System.IO;
using System.Text.Json;

// Operator-run diagnostics read their retained fixture root from the environment instead of
// committing a machine-specific path. Manual modes fail closed with a clear message.
internal static class DiagnosticPaths
{
    internal const string RootVariable = "SYNCSHARK_DIAG_ROOT";

    // Retained run identities (pair, bound operation, retry targets) belong to the operator's captured clone,
    // so they come from a manifest file named here rather than from committed source.
    internal const string JourneyManifestVariable = "SYNCSHARK_CLONED_JOURNEY_MANIFEST";

    internal static string RequireRoot()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(
                "Set " + RootVariable + " to the operator diagnostic fixture root before running this manual mode.");
        return Path.GetFullPath(root);
    }

    internal static JsonDocument? JourneyManifest()
    {
        var path = Environment.GetEnvironmentVariable(JourneyManifestVariable);
        return string.IsNullOrWhiteSpace(path) ? null : JsonDocument.Parse(File.ReadAllText(path));
    }

    // A retained identity the mode cannot run without: fail closed when the manifest lacks it.
    internal static string RequireJourneyIdentity(string name)
    {
        using var manifest = JourneyManifest()
            ?? throw new InvalidOperationException("Set " + JourneyManifestVariable +
                " to the captured retained-run manifest before running this manual mode.");
        return manifest.RootElement.TryGetProperty(name, out var value) &&
               value.GetString() is { Length: > 0 } identity
            ? identity
            : throw new InvalidOperationException("The retained-run manifest names no " + name + ".");
    }
}
