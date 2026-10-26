using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

// No registration, provider connection or hydration: only native state is substituted.
internal static class StubReplacementChecks
{
    internal static async Task RunAsync()
    {
        foreach (var scenario in new[] { "partial", "modified", "dirty", "offline-only", "invalid-state", "wrong-id", "no-partial", "partial-on-disk", "dehydrated", "writer", "cancel" })
            await CheckAsync(scenario);
        await ReplacementOffAsync();
        LocalChecks.Require(!V2Settings.Default.ReplaceOnlineOnlyStubs, "stub replacement remains default-off");
        Console.WriteLine("PASS B-169 offline stub gates (11 scenarios)");
    }

    private static async Task ReplacementOffAsync()
    {
        var root = LocalChecks.FreshRoot("stub-replacement-off");
        var full = Path.Combine(root, "stub.txt");
        LocalChecks.Seed(root, "stub.txt", "fixture bytes");
        File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.Offline);
        var nativeCalls = 0;
        WindowsCloudFilesProvider.StubStateForTest = _ => { nativeCalls++; return null; };
        try
        {
            using var known = new InMemoryStateStore();
            var snapshot = await LocalChecks.Inventory(root, known, TimeProvider.System)
                .FullAsync(1, _ => true, null!, CancellationToken.None);
            var item = snapshot.Items.Single(i => i.Path == "stub.txt");
            LocalChecks.Require(item.Flags == ItemFlags.Placeholder && nativeCalls == 0,
                "replacement off: metadata inventory keeps candidate placeholder without native proof");
            PlannerChecks.AssertStubLeftAlone(item);
            using var lease = PilotDirectoryLease.Open(root);
            var scan = await new PilotLocalScanner { CloudFilesAware = true, ReplaceOnlineOnlyStubs = false }
                .ScanAsync(lease, refreshPaths: ["stub.txt"]);
            LocalChecks.Require(scan.FilesHashed == 0 && scan.Decisions.Count == 0 && nativeCalls == 0,
                "replacement off: hashing scan neither reads candidate data nor queries native state nor raises notices");
            PlannerChecks.AssertStubLeftAlone(LocalItems.FromScan(scan.Entries["stub.txt"]));
        }
        finally
        {
            WindowsCloudFilesProvider.StubStateForTest = null;
            File.SetAttributes(full, FileAttributes.Normal);
        }
    }

    private static async Task CheckAsync(string scenario)
    {
        var root = LocalChecks.FreshRoot("stub-gate-" + scenario);
        var full = Path.Combine(root, "stub.txt");
        LocalChecks.Seed(root, "stub.txt", "fixture bytes");
        File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.Offline);
        var id = (ulong)PilotPath.ListedEntry(full).FileId;
        WindowsCloudFilesProvider.StubState? Query(SafeFileHandle handle)
        {
            if (scenario == "offline-only") return null;
            return new(scenario switch { "invalid-state" => uint.MaxValue, "no-partial" => 1u,
                    "partial-on-disk" => 0x31u, _ => 0x11u },
                scenario == "partial" ? 4096 : 0, scenario == "modified" ? 1 : 0,
                scenario == "dirty" ? 0 : 1, scenario == "wrong-id" ? id + 1 : id);
        }
        if (scenario == "offline-only")
        {
            using var held = SyncLocalFileAuthority.OpenOnlineOnlyStub(full);
            LocalChecks.Require(WindowsCloudFilesProvider.ReadStubState(held.Handle) is null,
                "real native query: OFFLINE alone supplies no placeholder evidence");
        }
        WindowsCloudFilesProvider.StubStateForTest = Query;
        try
        {
            using var known = new InMemoryStateStore();
            var snapshot = await LocalChecks.Inventory(root, known, TimeProvider.System,
                    settings: V2Settings.Default with { ReplaceOnlineOnlyStubs = true })
                .FullAsync(1, _ => true, null!, CancellationToken.None);
            var item = snapshot.Items.Single(i => i.Path == "stub.txt");
            var accepted = scenario is "dehydrated" or "writer" or "cancel";
            LocalChecks.Require(item.Flags == (accepted ? ItemFlags.Placeholder : ItemFlags.Unreadable),
                scenario + ": inventory requires positive, unmodified, fully dehydrated state");
            if (!accepted) PlannerChecks.AssertUnreadableStub(item);
            var writer = await LocalChecks.OpenWriter(root, Path.Combine(Path.GetDirectoryName(root)!, "recovery"), CancellationToken.None);
            var bytes = LocalChecks.Bytes("replacement bytes");
            // Force a stale planned stub as well: the writer must enforce the gate independently of inventory.
            var action = new SyncAction("stub-" + scenario, Planner.StubRule, ActionKind.Update, Side.A,
                item.Path, null, LocalWriterChecks.Source(bytes), item with { Flags = ItemFlags.Placeholder }, false, null);
            using var cancel = new CancellationTokenSource();
            System.Diagnostics.Process? competing = null;
            if (scenario == "writer")
            {
                var script = "$f=[IO.File]::Open('" + full.Replace("'", "''") +
                    "',[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete);" +
                    "[Console]::WriteLine('held');[Console]::ReadLine() | Out-Null;$f.Dispose()";
                competing = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true })!;
                LocalChecks.Require(await competing.StandardOutput.ReadLineAsync() == "held", "another process holds write access");
            }
            var validated = false;
            WindowsCloudFilesProvider.StubStateForTest = handle =>
            {
                validated = true;
                if (scenario == "dehydrated")
                {
                    try
                    {
                        using var write = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        throw new InvalidOperationException("a writer entered during final validation");
                    }
                    catch (IOException) { }
                }
                if (scenario == "cancel") cancel.Cancel();
                return Query(handle);
            };
            try
            {
                if (scenario == "dehydrated")
                {
                    var result = await writer.PutAsync(action, new MemoryStream(bytes), cancel.Token);
                    LocalChecks.Require(validated && result.ObjectId != item.ObjectId && File.ReadAllBytes(full).SequenceEqual(bytes),
                        "dehydrated foreign placeholder: exclusive validation then replacement");
                }
                else
                {
                    if (scenario == "cancel")
                        await LocalWriterChecks.ExpectAsync<OperationCanceledException>(() => writer.PutAsync(action, new MemoryStream(bytes), cancel.Token));
                    else
                        await LocalWriterChecks.ExpectAsync<IOException>(() => writer.PutAsync(action, new MemoryStream(bytes), cancel.Token));
                    LocalChecks.Require((ulong)PilotPath.ListedEntry(full).FileId == id,
                        scenario + ": refusal leaves original object in place");
                    if (scenario == "writer") LocalChecks.Require(!validated, "existing writer refused before final validation");
                    if (scenario == "cancel")
                    {
                        WindowsCloudFilesProvider.StubStateForTest = _ => new(0x11, 1, 0, 1, id);
                        await LocalWriterChecks.ExpectAsync<IOException>(() => writer.PutAsync(action, new MemoryStream(bytes), CancellationToken.None));
                        LocalChecks.Require((ulong)PilotPath.ListedEntry(full).FileId == id,
                            "retry after cancellation revalidates changed dehydration state, never reuses prior proof");
                    }
                }
            }
            finally
            {
                if (competing is not null)
                {
                    await competing.StandardInput.WriteLineAsync("release");
                    await competing.WaitForExitAsync();
                    competing.Dispose();
                }
            }
        }
        finally
        {
            WindowsCloudFilesProvider.StubStateForTest = null;
            File.SetAttributes(full, FileAttributes.Normal);
        }
    }
}
