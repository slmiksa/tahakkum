# Restores the secrets folder from secrets.enc on a new machine. Asks for the password chosen
# when it was encrypted, and refuses to run if the file was changed or the password is wrong.
#
#   powershell -ExecutionPolicy Bypass -File supabase-backend\tools\secrets-unlock.ps1
param(
    [string]$File = (Join-Path $PSScriptRoot '..\secrets.enc'),
    [string]$Target = (Join-Path $PSScriptRoot '..\secrets'),
    [switch]$Force,
    # For tests only; normally the password is asked for.
    [Security.SecureString]$Password
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

if ((Test-Path $Target) -and (Get-ChildItem $Target -File -ErrorAction SilentlyContinue) -and -not $Force) {
    throw "$Target already has files. Nothing was changed. Use -Force to replace them."
}

$data = [IO.File]::ReadAllBytes((Resolve-Path $File).Path)
if ([Text.Encoding]::ASCII.GetString($data, 0, 6) -ne 'TKSEC1') { throw "This is not an encrypted secrets file." }
$salt = $data[6..21]
$iterations = [BitConverter]::ToInt32($data, 22)
$iv = $data[26..41]
$cipherEnd = $data.Length - 33
$signedLength = $data.Length - 32

if (-not $Password) { $Password = Read-Host "Password for the secrets file" -AsSecureString }
$p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try { $secret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }

$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($secret, [byte[]]$salt, $iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
$keys = $kdf.GetBytes(64)
$secret = $null

# The check comes first: a wrong password or a changed file stops here, before any decryption.
$mac = (New-Object Security.Cryptography.HMACSHA256(,[byte[]]$keys[32..63])).ComputeHash($data, 0, $signedLength)
$stored = $data[$signedLength..($data.Length - 1)]
$diff = 0
for ($i = 0; $i -lt 32; $i++) { $diff = $diff -bor ($mac[$i] -bxor $stored[$i]) }
if ($diff -ne 0) { throw "Wrong password, or the file was changed. Nothing was written." }

$aes = [Security.Cryptography.Aes]::Create()
$aes.Key = $keys[0..31]; $aes.IV = [byte[]]$iv
$cipher = $data[42..$cipherEnd]
$plain = $aes.CreateDecryptor().TransformFinalBlock([byte[]]$cipher, 0, $cipher.Length)

New-Item -ItemType Directory -Force $Target | Out-Null
$zip = New-Object IO.Compression.ZipArchive((New-Object IO.MemoryStream(,$plain)), [IO.Compression.ZipArchiveMode]::Read)
foreach ($entry in $zip.Entries) {
    # Plain file names only, so nothing can be written outside the secrets folder.
    if ($entry.FullName -ne [IO.Path]::GetFileName($entry.FullName) -or $entry.FullName -eq '') { throw "Unexpected entry in the file: $($entry.FullName)" }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Target $entry.FullName), $true)
}
$count = $zip.Entries.Count
$zip.Dispose()
Write-Host "Restored $count files into $((Resolve-Path $Target).Path)"
