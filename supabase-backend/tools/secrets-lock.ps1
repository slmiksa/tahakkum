# Encrypts the secrets folder into secrets.enc, which is safe to keep in the repository.
# The password is typed here and never stored. Without it the file cannot be opened.
#
#   powershell -ExecutionPolicy Bypass -File supabase-backend\tools\secrets-lock.ps1
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\secrets'),
    [string]$Output = (Join-Path $PSScriptRoot '..\secrets.enc'),
    # For tests only; normally the password is asked for.
    [Security.SecureString]$Password
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$Source = (Resolve-Path $Source).Path
$files = Get-ChildItem -LiteralPath $Source -File
if ($files.Count -eq 0) { throw "Nothing to encrypt in $Source" }

function Plain([Security.SecureString]$s) {
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}

if (-not $Password) {
    Write-Host ""
    Write-Host "Choose a password for the encrypted secrets file (at least 12 characters)."
    Write-Host "Keep it somewhere safe: without it the file can never be opened."
    $first = Read-Host "Password" -AsSecureString
    $second = Read-Host "Repeat it" -AsSecureString
    if ((Plain $first) -cne (Plain $second)) { throw "The two passwords are different. Nothing was written." }
    $Password = $first
}
$secret = Plain $Password
if ($secret.Length -lt 12) { throw "The password is shorter than 12 characters. Nothing was written." }

# Pack the files into a zip in memory.
$zipStream = New-Object IO.MemoryStream
$zip = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
foreach ($f in $files) {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $f.Name)
}
$zip.Dispose()
$plain = $zipStream.ToArray()

# AES-256-CBC, then HMAC-SHA256 over everything before it, with keys from PBKDF2-SHA256.
$iterations = 600000
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$salt = New-Object byte[] 16; $rng.GetBytes($salt)
$iv = New-Object byte[] 16; $rng.GetBytes($iv)
$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($secret, $salt, $iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
$keys = $kdf.GetBytes(64)
$secret = $null
$aes = [Security.Cryptography.Aes]::Create()
$aes.Key = $keys[0..31]; $aes.IV = $iv
$cipher = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length)

$header = [Text.Encoding]::ASCII.GetBytes('TKSEC1')
$body = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter($body)
$w.Write($header); $w.Write($salt); $w.Write([int]$iterations); $w.Write($iv); $w.Write($cipher)
$w.Flush()
$signed = $body.ToArray()
$mac = (New-Object Security.Cryptography.HMACSHA256(,[byte[]]$keys[32..63])).ComputeHash($signed)
[IO.File]::WriteAllBytes($Output, $signed + $mac)
Write-Host ""
Write-Host "Encrypted $($files.Count) files into $Output"
