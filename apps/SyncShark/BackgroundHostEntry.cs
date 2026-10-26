using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

internal static class BackgroundHostEntry
{
    internal static AutoSyncHostClient Client() => new(AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile));

    internal static async Task<int> RunAsync()
    {
        var logRoot = ProductRootOrNull();
        var client = Client();
        if (await client.TrySendAsync(AutoSyncHostClient.Request("status"), TimeSpan.FromMilliseconds(300)) is { } existing)
        {
            Console.WriteLine($"AutoSync already running (PID {existing.Host.Pid}). Attached; no second engine created.");
            if (logRoot is { } root) AutoSyncLog.Append(root, "host shutdown: attached to existing owner");
            return 0;
        }
        // The owner is silent on stdout; stderr is durable so an externally ended host leaves evidence.
        Console.SetOut(TextWriter.Null);
        Console.SetError(logRoot is { } logTarget ? AutoSyncLog.CreateStderrWriter(logTarget) : TextWriter.Null);
        try
        {
            PilotInstanceLock? authority = null;
            using var wake = new SystemWakeSource();
            await using var host = AutoSyncBackgroundHost.Start(
                AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile),
                () =>
                {
                    authority = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
                    try { AutoSyncProcess.ClearExistingOwnerNotice(AutoSyncProcess.ExistingOwnerNoticePath); }
                    catch (Exception noticeError) { Console.Error.WriteLine("Could not clear the startup notice: " + noticeError.Message); }
                    return authority;
                },
                () => new ProductionAutoSyncEngine(authority!, SyncDataLocation.ProductRoot, wake),
                stopSupervisor: ProcessEntry.StopSupervisor);
            await host.Completion.ConfigureAwait(false);
            if (logRoot is { } root)
                AutoSyncLog.Append(root, "host shutdown: " + host.ShutdownReason);
            return 0;
        }
        catch (AutoSyncHostReservationException)
        {
            // FirstPipeInstance can lose after the initial status probe. Never acquire a
            // second store/engine; attach to the winning starter as it becomes ready.
            return await AttachToOwnerAsync(client, logRoot);
        }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 })
        {
            return await AttachToOwnerAsync(client, logRoot);
        }
        catch (Exception error)
        {
            if (logRoot is { } root)
                AutoSyncLog.Append(root, "host shutdown: unexpected termination: " + SyncCli.DescribeException(error));
            return (int)SyncCliExitCode.Failed;
        }
    }

    private static string? ProductRootOrNull()
    {
        try { return SyncDataLocation.ProductRoot; }
        catch (Exception) { return null; } // the engine factory reports the same invalid location
    }

    private static async Task<int> AttachToOwnerAsync(AutoSyncHostClient client, string? logRoot)
    {
        var reason = "owner attach failed";
        try
        {
            var attached = await client.TrySendAsync(AutoSyncHostClient.Request("status"), TimeSpan.FromSeconds(5));
            reason = attached is not null ? "attached to existing owner" : "existing owner unavailable";
            return attached is not null ? 0 : (int)SyncCliExitCode.Busy;
        }
        finally
        {
            if (logRoot is { } root) AutoSyncLog.Append(root, "host shutdown: " + reason);
        }
    }

    internal static async Task<AutoSyncHostReply> StartAsync(CancellationToken ct)
    {
        var client = Client();
        using var process = AutoSyncProcess.StartDetached(
            Environment.ProcessPath ?? throw new IOException("Executable path unavailable."), supervisor: true, warning: Console.Error.WriteLine);
        // Only observations are retried. Mutations are sent exactly once by their caller.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await client.TrySendAsync(AutoSyncHostClient.Request("status"), TimeSpan.FromSeconds(1), ct) is { } reply)
            {
                return reply;
            }
            if (process.HasExited) break;
        }
        throw new IOException("The host did not become ready. Check for an existing GUI or maintenance owner; no mutation was replayed.");
    }

    private static void WriteReply(TextWriter output, AutoSyncHostReply reply, bool json)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(reply, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        else
        {
            output.WriteLine($"AutoSync {reply.State}; PID {reply.Host.Pid}; uptime {reply.Host.UptimeSeconds:N0}s.");
            if (reply.Data is { } data) output.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            if (reply.Error is { } error) output.WriteLine(error.Message);
        }
    }

    internal static async Task<int?> TryCliAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var invocation = SyncCliRegistry.Parse(args);
        var auto = invocation.Command.StartsWith("autosync ", StringComparison.Ordinal);
        AutoSyncHostReply? reply;
        if (auto)
        {
            var command = invocation.Command[9..];
            if (command is "start" or "status")
            {
                try { if (File.Exists(AutoSyncProcess.ExistingOwnerNoticePath)) error.WriteLine(File.ReadAllText(AutoSyncProcess.ExistingOwnerNoticePath)); }
                catch (Exception noticeError) { error.WriteLine("Could not read the startup notice: " + noticeError.Message); }
            }
            if (command == "stop") ProcessEntry.StopSupervisor();
            if (command is "install-startup" or "uninstall-startup")
            {
                var running = Environment.ProcessPath ?? throw new IOException("Executable path unavailable.");
                var installed = SyncSharkRuntimePaths.InstalledExecutablePath;
                var executable = running;
                if (command == "install-startup" && File.Exists(installed))
                {
                    var currentVersion = FileVersionInfo.GetVersionInfo(running).ProductVersion;
                    var installedVersion = FileVersionInfo.GetVersionInfo(installed).ProductVersion;
                    executable = AutoSyncProcess.SelectStartupExecutable(running, currentVersion, installed,
                        installedVersion, invocation.Flag("--this-build"));
                    if (!string.Equals(currentVersion, installedVersion, StringComparison.Ordinal))
                        error.WriteLine($"Startup versions differ: running {running} ({currentVersion}); installed {installed} ({installedVersion}). Registered {executable}. Use --this-build to select the running executable.");
                }
                if (command == "install-startup" && string.Equals(executable, running, StringComparison.OrdinalIgnoreCase))
                    error.WriteLine("Registering the running build. Keep this executable in place, or rerun install-startup after installing an update.");
                output.WriteLine(AutoSyncProcess.RegisterStartup(executable, command == "install-startup"));
                return 0;
            }
            var options = invocation.Options ?? new Dictionary<string, string?>();
            int? minutes = options.TryGetValue("--minutes", out var text) && int.TryParse(text, out var parsed) ? parsed : null;
            long cursor = options.TryGetValue("--after", out var after) && long.TryParse(after, out var value) ? value : 0;
            var request = AutoSyncHostClient.Request(command, invocation.PairId, options.ContainsKey("--all"), minutes,
                invocation.Limit, cursor);
            reply = command == "start" ? await StartAsync(ct) :
                await Client().TrySendAsync(request, TimeSpan.FromMilliseconds(500), ct);
            if (reply is null)
            {
                output.WriteLine(JsonSerializer.Serialize(new { running = false, detail = "Host is not running. Use autosync start; this command did not launch it." }));
                return command is "status" or "history" or "logs" or "stop" ? 0 : (int)SyncCliExitCode.Busy;
            }
            WriteReply(output, reply, invocation.Json);
            if (command == "logs" && options.ContainsKey("--follow"))
            {
                while (!ct.IsCancellationRequested)
                {
                    if (reply.Data is { ValueKind: JsonValueKind.Array } data)
                        foreach (var row in data.EnumerateArray())
                            if (row.TryGetProperty("seq", out var seq)) cursor = Math.Max(cursor, seq.GetInt64());
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    var next = await Client().TrySendAsync(AutoSyncHostClient.Request("logs", limit: invocation.Limit, after: cursor),
                        TimeSpan.FromMilliseconds(500), ct);
                    if (next is null) break;
                    reply = next;
                    WriteReply(output, reply, invocation.Json);
                }
            }
        }
        else
        {
            if (SyncCli.IsStateFreeInvocation(args)) return null;
            var request = AutoSyncHostClient.Request("cli") with { CliArgs = args };
            reply = await Client().TrySendAsync(request, TimeSpan.FromMilliseconds(300), ct);
            if (reply is null) return null;
            if (reply.Ok && reply.Data is { } payload)
            {
                var result = payload.Deserialize<HostCliOutput>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("The host CLI result was empty; inspect status before retrying.");
                output.Write(result.Stdout);
                error.Write(result.Stderr);
                return result.ExitCode;
            }
            WriteReply(output, reply, invocation.Json);
        }
        if (!reply.Ok) error.WriteLine(reply.Error?.Message);
        return reply.Ok ? 0 : (int)SyncCliExitCode.Failed;
    }
}
