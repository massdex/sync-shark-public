using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Preserves the existing CLI envelope and exit code across the shared host boundary.</summary>
public sealed record HostCliOutput(int ExitCode, string Stdout, string Stderr);
