[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SpecCommit,
    [string]$SpecDir,
    [string]$OutputPath
)

# Regenerates the embedded user-token Dropbox route table (dropbox-cli.md §2.2) from the Dropbox API spec
# at one exact commit. It ports design/dropbox-routes.parse-stone.ps1 (route declarations and attrs) and
# design/dropbox-routes.build-table.ps1 (the policy rule and JSON layout). Updating the table means running
# this with a newer commit, reviewing the diff and committing it. Tests never touch the network.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $repositoryRoot 'src\SyncShark.Core\Dropbox\dropbox-routes.json' }
$specRepository = 'dropbox/dropbox-api-spec'

# Download the spec's .stone files for this exact commit unless a prepared folder is supplied.
$downloaded = $false
if (-not $SpecDir) {
    $SpecDir = Join-Path ([IO.Path]::GetTempPath()) ("syncshark-dropbox-spec-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $SpecDir | Out-Null
    $downloaded = $true
    $headers = @{ 'User-Agent' = 'SyncShark-Update-DropboxRoutes' }
    $tree = Invoke-RestMethod -Headers $headers -Uri (
        "https://api.github.com/repos/$specRepository/git/trees/${SpecCommit}?recursive=1")
    $stones = @($tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -like '*.stone' })
    if ($stones.Count -eq 0) { throw "No .stone files were found at commit $SpecCommit." }
    foreach ($stone in $stones) {
        $raw = "https://raw.githubusercontent.com/$specRepository/$SpecCommit/$($stone.path)"
        Invoke-WebRequest -Headers $headers -Uri $raw -OutFile (Join-Path $SpecDir (Split-Path $stone.path -Leaf))
    }
}

try {
    # --- parse-stone: one row per route declaration, defaults from stone_cfg (auth=user host=api style=rpc).
    $rows = @()
    foreach ($file in Get-ChildItem $SpecDir -Filter *.stone) {
        $lines = Get-Content $file.FullName
        $ns = $null
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -match '^namespace\s+(\w+)') { $ns = $Matches[1]; continue }
            if ($line -notmatch '^route\s+([\w/]+)(?::(\d+))?\s*\(\s*([^,]+),\s*([^,]+),\s*([^)]+)\)(.*)$') { continue }
            $name = $Matches[1]; $ver = if ($Matches[2]) { [int]$Matches[2] } else { 1 }
            $arg = $Matches[3].Trim(); $rest = $Matches[6]
            $attrs = @{ auth = 'user'; host = 'api'; style = 'rpc'; is_preview = 'false'; scope = '' }
            $j = $i + 1
            while ($j -lt $lines.Count -and $lines[$j] -notmatch '^(route|struct|union|alias|namespace|import|annotation)\b') {
                if ($lines[$j] -match '^\s+(auth|host|style|is_preview|scope|select_admin_mode)\s*=\s*"?([^"]*)"?\s*$') { $attrs[$Matches[1]] = $Matches[2].Trim() }
                $j++
            }
            $url = if ($ver -gt 1) { "$name" + "_v$ver" } else { $name }
            $rows += [pscustomobject]@{
                route = "$ns/$url"; namespace = $ns; host = $attrs.host; style = $attrs.style
                auth = ($attrs.auth -replace '\s', ''); scope = $attrs.scope
                readOnly = [bool]($attrs.scope -match '\.read$')
                argVoid = ($arg -eq 'Void'); deprecated = [bool]($rest -match 'deprecated'); preview = ($attrs.is_preview -eq 'true')
            }
        }
    }

    # --- build-table: the policy rule (design §2.2) and the JSON layout.
    $deniedRoutes = @{ 'auth/token/revoke' = 'revokes the stored Sync Shark credential; use accounts disconnect' }
    $writeOverrides = @{ 'files/get_temporary_link' = 'mints a bearer download URL' }
    function Special($r) {
        $n = $r.route
        if ($n -match '/longpoll$') { return 'longpoll' }
        if ($n -match '(/check(_v2)?|/check_\w*job_status)$') { return 'async-check' }
        if ($n -match '/upload_session/') { return 'upload-session' }
        if ($n -match '/continue(_v2)?$') { return 'continue' }
        return $null
    }
    $rows = $rows | Where-Object { $_.auth -match 'user|noauth' } | Sort-Object route
    $routes = foreach ($r in $rows) {
        $special = Special $r
        $policy = if ($deniedRoutes.ContainsKey($r.route)) { 'denied' }
                  elseif ($writeOverrides.ContainsKey($r.route)) { 'write' }
                  elseif ($r.scope -match '\.read$' -or $special -eq 'async-check') { 'read' }
                  else { 'write' }
        $o = [ordered]@{ name = $r.route; host = $r.host; style = $r.style; auth = $r.auth; scope = $r.scope; policy = $policy }
        if ($special) { $o.special = $special }
        if ($r.argVoid -eq 'True') { $o.voidArg = $true }
        if ($r.deprecated -eq 'True') { $o.deprecated = $true }
        if ($r.preview -eq 'True') { $o.preview = $true }
        [pscustomobject]$o
    }
    $doc = [ordered]@{
        source = 'github.com/dropbox/dropbox-api-spec'; specCommit = $SpecCommit
        hosts = [ordered]@{ api = 'https://api.dropboxapi.com/2/'; content = 'https://content.dropboxapi.com/2/'; notify = 'https://notify.dropboxapi.com/2/' }
        excluded = 'routes whose auth is team or app only (need a team or app credential, not the user token)'
        routes = @($routes | Sort-Object name)
    }
    $json = $doc | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($OutputPath, $json.Replace("`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding $false))

    $all = @($routes | Sort-Object name)
    Write-Output ("Wrote {0} routes to {1}" -f $all.Count, $OutputPath)
    "routes=" + $all.Count + " read=" + @($all | ? policy -eq 'read').Count + " write=" + @($all | ? policy -eq 'write').Count + " denied=" + @($all | ? policy -eq 'denied').Count
    "async-check=" + @($all | ? special -eq 'async-check').Count + " continue=" + @($all | ? special -eq 'continue').Count + " upload-session=" + @($all | ? special -eq 'upload-session').Count + " longpoll=" + @($all | ? special -eq 'longpoll').Count
}
finally {
    if ($downloaded) { Remove-Item -Recurse -Force $SpecDir -ErrorAction SilentlyContinue }
}
