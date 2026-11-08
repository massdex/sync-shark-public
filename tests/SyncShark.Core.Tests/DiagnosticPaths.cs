using System;
using System.IO;

// Operator-run diagnostics read their retained fixture root from the environment instead of
// committing a machine-specific path. Manual modes fail closed with a clear message.
internal static class DiagnosticPaths
{
    internal const string RootVariable = "SYNCSHARK_DIAG_ROOT";

    internal static string RequireRoot()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(
                "Set " + RootVariable + " to the operator diagnostic fixture root before running this manual mode.");
        return Path.GetFullPath(root);
    }
}
