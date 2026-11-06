param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$exe = Join-Path $package 'StaleBread.exe'
foreach ($name in @('StaleBread.exe','LICENSE','USAGE.md','THIRD-PARTY-NOTICES.md','Assets/LICENSES.md','checksum.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $name) -PathType Leaf)) { throw "Missing $name" }
}
foreach ($name in @('CC-BY-4.0.txt','dotnet-runtime-LICENSE.txt','dotnet-runtime-NOTICES.txt','dotnet-winforms-LICENSE.txt','dotnet-wpf-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package "Licenses/$name") -PathType Leaf)) { throw "Missing license $name" }
}
if (Test-Path -LiteralPath (Join-Path $package 'Licenses/Licenses')) { throw 'Repeated publish nested the license directory' }
$unexpected = Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -match '\.pdb$|testhost|TestChild|xunit|\.deps\.json$|\.runtimeconfig\.json$' }
if ($unexpected) { throw 'Consumer package contains test/debug/unbundled runtime files' }
$expected = Get-Content -Raw -LiteralPath (Join-Path $package 'checksum.json') | ConvertFrom-Json
if ($expected.File -cne 'StaleBread.exe' -or $expected.Algorithm -cne 'SHA256') { throw 'Checksum must identify portable filename StaleBread.exe and algorithm SHA256' }
if (@($expected.PSObject.Properties.Name | Where-Object { $_ -notin @('File','Algorithm','Hash') }).Count -ne 0) { throw 'Checksum contains unexpected fields; absolute paths must not be distributed' }
if ($expected.Hash -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid SHA256 checksum format' }
$actual = Get-FileHash -LiteralPath $exe -Algorithm SHA256
if ($expected.Hash -ne $actual.Hash) { throw 'Checksum mismatch' }
$bytes = [System.IO.File]::ReadAllBytes($exe)
$pe = [BitConverter]::ToInt32($bytes, 0x3c)
if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664) { throw 'Not x64' }
if ([BitConverter]::ToUInt16($bytes, $pe + 24 + 68) -ne 2) { throw 'Not Windows GUI subsystem' }
[pscustomobject]@{ File = $exe; Bytes = $bytes.Length; SHA256 = $actual.Hash; Architecture = 'x64'; Subsystem = 'Windows GUI'; Signature = (Get-AuthenticodeSignature -LiteralPath $exe).Status.ToString() } | ConvertTo-Json
