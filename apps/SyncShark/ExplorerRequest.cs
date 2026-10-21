using System.IO;
using System.Text.Json;

namespace Massdex.SyncShark;

internal sealed record ExplorerRequest(string Operation, string Path)
{
    // Navigation does not wait for a network check or startup recovery. Operations
    // that can change files must wait for recovery, and fail visibly if it fails.
    internal bool RequiresCompletedStartup => Operation is "sync" or "offline" or "online-only";

    internal static ExplorerRequest? Parse(string[] arguments)
    {
        if (!arguments.Contains("--explorer", StringComparer.Ordinal)) return null;
        if (arguments.Length != 3 || arguments[0] != "--explorer")
            throw new InvalidDataException("Explorer actions accept exactly one selected item.");
        return Validate(new ExplorerRequest(arguments[1], arguments[2]));
    }

    internal static ExplorerRequest Validate(ExplorerRequest request)
    {
        if (request.Operation is not ("open" or "web" or "share" or "sync" or "offline" or "online-only") ||
            string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 32700 ||
            request.Path.IndexOfAny(['\0', '"', '\r', '\n']) >= 0 ||
            !System.IO.Path.IsPathFullyQualified(request.Path) || request.Path.StartsWith("\\\\", StringComparison.Ordinal) ||
            request.Path.Length < 3 || request.Path[1] != ':' || request.Path[2] != '\\' ||
            request.Path[2..].Contains(':'))
            throw new InvalidDataException("Choose one ordinary local item inside a Sync Shark folder pair.");
        return request with { Path = System.IO.Path.GetFullPath(request.Path) };
    }

    internal string Serialize() => JsonSerializer.Serialize(this);
    internal static ExplorerRequest Deserialize(string input) => Validate(JsonSerializer.Deserialize<ExplorerRequest>(input)
        ?? throw new InvalidDataException("The Explorer request is empty."));
}
