namespace Massdex.SyncShark.V2;

/// <summary>A bounded command, never a method name to invoke through reflection.</summary>
public sealed record AutoSyncHostRequest(int Version, string RequestId, string Command,
    string? PairId = null, bool All = false, int? Minutes = null, int Limit = 50, long After = 0, string[]? CliArgs = null)
{
    internal void Validate()
    {
        if (Version != AutoSyncHostProtocol.Version) throw new InvalidDataException("Host protocol version mismatch; use the same installed release.");
        if (!Guid.TryParseExact(RequestId, "N", out _)) throw new InvalidDataException("A request identity is required.");
        if (Command == "cli")
        {
            if (CliArgs is null || CliArgs.Length > 128 || CliArgs.Sum(value => value?.Length ?? 0) > 65536)
                throw new InvalidDataException("A bounded CLI invocation is required.");
            var invocation = SyncCliRegistry.Parse(CliArgs);
            if (invocation.Command.StartsWith("autosync ", StringComparison.Ordinal))
                throw new InvalidDataException("Use the host command, not a nested AutoSync invocation.");
        }
        else if (CliArgs is not null) throw new InvalidDataException("CLI arguments require the cli command.");
        if (Command is not ("status" or "stop" or "enable" or "disable" or "interval" or "history" or "now" or "logs" or "cli"))
            throw new InvalidDataException("Unknown AutoSync host command.");
        if (PairId is not null && (string.IsNullOrWhiteSpace(PairId) || PairId.Length > 256))
            throw new InvalidDataException("An exact bounded pair identity is required.");
        if (Command is "enable" or "disable")
        {
            if (All == (PairId is not null)) throw new InvalidDataException("Choose an exact pair or all pairs, not both.");
        }
        else if (All) throw new InvalidDataException("All pairs is only valid for enable and disable.");
        if (Command is "history" or "now" && PairId is null) throw new InvalidDataException("This command requires an exact pair.");
        if (Command is "status" or "stop" or "interval" or "logs" && PairId is not null)
            throw new InvalidDataException("This command does not take a pair.");
        if (Command == "interval")
        {
            if (Minutes is not (>= SyncAutomationStore.MinimumIntervalMinutes and <= SyncAutomationStore.MaximumIntervalMinutes))
                throw new InvalidDataException("The interval must be between 1 and 1440 minutes.");
        }
        else if (Minutes is not null) throw new InvalidDataException("Minutes is only valid for interval.");
        if (Limit is < 1 or > 200 || After < 0) throw new InvalidDataException("Use a limit of 1 through 200 and a nonnegative cursor.");
    }
}
