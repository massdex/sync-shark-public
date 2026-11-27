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
            if (!await server.ClaimAsync(lifetime.Token)) { HostServer.Ready(args); return 0; }
            Console.CancelKeyPress += (_,e) => { e.Cancel = true; lifetime.Cancel(); };
            var settings = SettingsParser.Load(); var clock = TimeProvider.System;
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = Path.Combine(localData, ResourceNames.ProductFolder);
            var move = new StoreMove(); var connections = new Connections(); var readRoot = root;

            var logs = new LogFiles(); var local = LocalSide.Factory(out var reserve); var bin = new RecycleBin(settings);
            using var transport = DropboxApi.CreateHttpClient(clock);
            var credentials = new CredentialStore(); var signIn = new SignIn();
            var accounts = new DropboxApi(transport, clock, settings, e => Console.Error.WriteLine(e.Detail));
            var verified = new System.Collections.Concurrent.ConcurrentDictionary<string,(string AccountId,string RootNamespaceId,string HomeNamespaceId)>(StringComparer.Ordinal);
            Host? host = null;
            host = new Host(async (create,ct) => await Store.OpenAsync(root,create,settings,ct), async (pair,store,scope,events,ct) => {
                if (!connections.Read(Volatile.Read(ref readRoot)).Contains(pair.ConnectionId,StringComparer.Ordinal))
                    throw new EngineRequestException(TextKey.AccountMissing,(TextArg.Account,pair.ConnectionId));
                var bound = pair.Roots?.Dropbox;
                var identity = bound != null ? (bound.AccountId,bound.RootNamespaceId,bound.HomeNamespaceId) :
                    verified.TryGetValue(pair.ConnectionId, out var known) ? known :
                    await SignIn.VerifyAccountAsync(accounts.SendAsync,credentials,pair.ConnectionId,ct);
                var token = SignIn.AccessTokenSource(accounts.SendAsync,credentials,pair.ConnectionId,identity.AccountId,identity.RootNamespaceId,identity.HomeNamespaceId);
                var api = accounts.CreateAccountScope(identity.AccountId,token,identity.RootNamespaceId);
                return (local(pair,scope,settings,store), new DropboxSide(pair,scope,api,new Dispatcher(api,store,job => host!.InstalledAsync(job,CancellationToken.None))));
            }, settings, logs, clock, async ct => {
                var account = await signIn.ConnectAccountAsync(accounts,ct);
                var id = account.ConnectionId;
                verified[id] = (account.AccountId,account.RootNamespaceId,account.HomeNamespaceId);
                connections.SetConnected(root,ref readRoot,id,true); return id;
            }, async (id,ct) => {
                ct.ThrowIfCancellationRequested();
                connections.SetConnected(root,ref readRoot,id,false);
                verified.TryRemove(id,out _);
                await SignIn.DisconnectAsync((legacy,token) => SignIn.OpenIdentityAsync(accounts.SendAsync,id,legacy,token),credentials,id,ct);
            }, (volume,type,ct) => bin.TrimAsync(volume,type,clock.GetUtcNow(),ct), reserve);
            bool refused = false;
            string? startupMessage;
            try {
                string? source = null, appendLog = null;
                startupMessage = await move.MoveWithOutcomeAsync(root,root,(from,to,ids) =>
                    connections.Verify(from,to,ids,path => { source=from; appendLog=path; }),lifetime.Token);
                if (source != null && appendLog != null) {
                    readRoot = source;
                    var warning = Text.Get(TextKey.DifferingConnections,(TextArg.Path,appendLog));
                    startupMessage = startupMessage == null ? warning : warning + Environment.NewLine + startupMessage;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { startupMessage=e.Message; refused=true; }
            if (startupMessage != null) host.SetStartupMessage(startupMessage,refused);   // Status carries it; a console write here could block startup
            var serving = server.ServeAsync(host, lifetime.Token);
            HostServer.Ready(args);
            try {
                if (refused) { await serving; return 1; }
                var startup = host.StartAsync(lifetime.Token);
                if (await Task.WhenAny(startup, serving) == serving) { lifetime.Cancel(); await serving; }
                await startup;
                await serving;
            } finally { await lifetime.CancelAsync(); await serving; }
            return 0;

        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
