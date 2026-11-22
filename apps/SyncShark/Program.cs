using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark;

/// <summary>Claim the host, settle its published store, and serve while finite startup recovery finishes.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource();
        using var server = new HostServer();
        try {
            if (!await server.ClaimAsync(lifetime.Token)) return 0;
            Console.CancelKeyPress += (_,e) => { e.Cancel = true; lifetime.Cancel(); };
            var settings = SettingsParser.Load(); var clock = TimeProvider.System;
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = Path.Combine(localData, ResourceNames.ProductFolder);
            var move = new StoreMove(); var connections = new Connections();
            await move.MoveAsync(root, root, connections.Verify, lifetime.Token);
            var logs = new LogFiles(); var local = LocalSide.Factory();
            using var transport = new HttpClient();
            var credentials = new CredentialStore(); var signIn = new SignIn();
            var accounts = new DropboxApi(transport, clock, settings, e => Console.Error.WriteLine(e.Detail));
            var host = new Host(Open, (pair,store,scope,events) => {
                var api = new DropboxApi(transport, clock, settings, events);
                return (local(pair,scope,settings,store), new DropboxSide(pair,scope,api,new Dispatcher(api,store)));
            }, settings, logs, clock, ct => signIn.ConnectAsync(accounts,ct), (id,ct) => {
                ct.ThrowIfCancellationRequested(); credentials.Remove(id);
                connections.Save(root, connections.Read(root).Where(key => key != id).ToArray()); return Task.CompletedTask;
            });
            var serving = server.ServeAsync(host, lifetime.Token);
            try {
                var startup = host.StartAsync(lifetime.Token);
                if (await Task.WhenAny(startup, serving) == serving) { lifetime.Cancel(); await serving; }
                var bin = new RecycleBin();
                foreach (var volume in await startup) await bin.TrimAsync(volume.VolumeRoot, volume.Type, clock.GetUtcNow(), lifetime.Token);
                await serving;
            } finally { await lifetime.CancelAsync(); await serving; }
            return 0;

            async Task<IStore?> Open(bool create, CancellationToken ct) {
                if (create) await move.CreateAsync(root, ct);
                try { return new Store(ResourceNames.DatabasePath(localData), false, settings); }
                catch (FileNotFoundException) when (!create) { return null; }
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
