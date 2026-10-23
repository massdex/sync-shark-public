using System.Diagnostics;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Read-only explicit-retry proof. Empty hidden/non-admin output is never absence evidence.</summary>
public sealed class DefenderExclusions : IDefenderExclusions
{
    public async Task<DefenderExclusionView> ReadAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new(false, []);
        const string script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
            "$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); " +
            "$p=Get-MpPreference -ErrorAction Stop; $v=$p.PSObject.Properties['HideExclusionsFromLocalAdmins']; " +
            "if (-not $admin -or $null -eq $v -or $v.Value -isnot [bool] -or $v.Value -ne $false) { exit 2 }; " +
            "$p.ExclusionPath | ForEach-Object { ConvertTo-Json -InputObject @{Resource=$_} -Compress }; Write-Output 'COMPLETE'";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Process? process = null;
        var evidence = new List<DefenderDetection>();
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            });
            if (process is null) return new(false, []);
            var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var complete = await DefenderHistory.ReadStreamAsync(process.StandardOutput, evidence, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            return new(complete && process.ExitCode == 0, evidence.Select(e => e.Resource).ToArray());
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        { return new(false, []); }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }
}
