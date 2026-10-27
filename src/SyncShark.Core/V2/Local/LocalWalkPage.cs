using System.Text.Json.Serialization;

namespace Massdex.SyncShark.V2;

internal sealed record LocalWalkPage(string Directory, string[] AddedDirectories,
    PilotLocalEntry[] Entries, PilotDecision[] Decisions, LocalWalkSelection[] Selections,
    long Bytes, int Found)
{
    [JsonRequired] public int Version { get; init; } = 3;
    public PilotDirectoryIdentity? DirectoryIdentity { get; init; }
    public PilotDirectoryIdentity?[] AddedDirectoryIdentities { get; init; } = [];
}
