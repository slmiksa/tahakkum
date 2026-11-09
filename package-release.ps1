# Creates the files handed over, in release\: tahakkum-dashboard.zip, tahakkum-client.zip, and the two server installers.
# Zip entries use forward slashes, so they also unpack correctly on Linux.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function New-Zip($zipPath, $entries) {
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($name in $entries.Keys) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$name], $name)
        }
    }
    finally { $zip.Dispose() }
    Get-Item $zipPath | Select-Object Name, Length
}

$root = $PSScriptRoot
$release = Join-Path $root 'release'
New-Item -ItemType Directory -Force $release | Out-Null

$dashboard = @{}
foreach ($f in 'index.html', 'app.js', 'config.js', 'styles.css', 'logo.svg', 'icon.svg', 'wordmark.png', 'server.mjs', '.htaccess', 'nginx-example.conf', 'README.md', 'vendor/supabase.js') {
    $dashboard[$f] = Join-Path (Join-Path $root 'dashboard') $f
}
New-Zip (Join-Path $release 'tahakkum-dashboard.zip') $dashboard

$exe = Join-Path $root 'client-program\dist\Tahakkum.exe'
$clientEntries = @{
    'Tahakkum.exe' = $exe
    'README.txt'   = (Join-Path $root 'client-program\customer-readme.txt')
}
# Android TV control needs Google's adb. It is not in git; drop platform-tools next to the exe
# (client-program\platform-tools) and it rides along in the client zip. Without it, every other
# feature still works and only the Android TV button reports adb missing.
$adbDir = Join-Path $root 'client-program\platform-tools'
if (Test-Path $adbDir) {
    foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll') {
        $p = Join-Path $adbDir $f
        if (Test-Path $p) { $clientEntries["platform-tools/$f"] = $p }
    }
} else {
    Write-Host "Note: platform-tools (adb) folder not found; the Android tool will not be bundled. See README to download it." -ForegroundColor Yellow
}
New-Zip (Join-Path $release 'tahakkum-client.zip') $clientEntries

# One script for the relay server, with the sources inside it. It carries only the license
# PUBLIC key. Unix line endings: bash cannot run the file otherwise.
$key = Get-Content (Join-Path $root 'supabase-backend\secrets\license-signing-key.json') -Raw | ConvertFrom-Json
function Get-Base64($path) {
    $text = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
    (($text -split '(.{76})') | Where-Object { $_ }) -join "`n"
}
$script = [IO.File]::ReadAllText((Join-Path $root 'relay-server\install-template.sh')).Replace("`r`n", "`n")
$script = $script.Replace('__KEY_X__', $key.x).Replace('__KEY_Y__', $key.y)
$script = $script.Replace('__SERVER_B64__', (Get-Base64 (Join-Path $root 'relay-server\RelayServer.cs')))
$script = $script.Replace('__PROTOCOL_B64__', (Get-Base64 (Join-Path $root 'client-program\src\Protocol.cs')))
$script = $script.Replace('__TOKEN_B64__', (Get-Base64 (Join-Path $root 'client-program\src\LicenseToken.cs')))
$out = Join-Path $release 'tahakkum-relay-install.sh'
[IO.File]::WriteAllText($out, $script, (New-Object Text.UTF8Encoding($false)))
Get-Item $out | Select-Object Name, Length

# The same for the dashboard, for running it on your own server.
$blocks = foreach ($f in 'index.html', 'app.js', 'config.js', 'styles.css', 'logo.svg', 'icon.svg', 'wordmark.png', 'server.mjs', 'vendor/supabase.js') {
    "base64 -d > `"`$APP/$f`" <<'FILE'`n" + (Get-Base64 (Join-Path (Join-Path $root 'dashboard') $f)) + "`nFILE"
}
$script = [IO.File]::ReadAllText((Join-Path $root 'dashboard\install-template.sh')).Replace("`r`n", "`n")
$script = $script.Replace('__FILES__', ($blocks -join "`n"))
$out = Join-Path $release 'tahakkum-dashboard-install.sh'
[IO.File]::WriteAllText($out, $script, (New-Object Text.UTF8Encoding($false)))
Get-Item $out | Select-Object Name, Length
