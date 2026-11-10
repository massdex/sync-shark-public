namespace Massdex.SyncShark;

/// <summary>Runs state-free setup commands without opening the sync runtime.</summary>
internal static class SetupCli
{
    internal static async Task<int> RunAsync(SyncCliInvocation invocation, string currentVersion,
        TextWriter standardOutput, TextWriter standardError, CancellationToken cancellationToken)
    {
        var release = await SetupUpdates.CheckAsync(cancellationToken);
        string? installer = null;
        if (invocation.Command == "setup apply")
        {
            if (invocation.Update != release.Version)
                throw new SyncCliUsageException(SetupMessages.Text("cli.versionMatch"));
            if (!SyncPublicUpdates.IsNewer(Version.Parse(release.Version), Version.Parse(currentVersion)))
                throw new SyncCliUsageException(SetupMessages.Text("cli.newer"));
            installer = await SetupUpdates.ApplyAsync(release, cancellationToken, standardError);
        }
        return SyncCli.WriteSuccess(standardOutput, invocation, new { release.Version, installer },
            installer is null ? SetupMessages.Text("cli.latest", release.Version) : SetupMessages.Text("cli.started", installer));
    }
}
