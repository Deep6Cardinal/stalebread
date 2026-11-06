param([ValidateSet('Release')][string]$Configuration = 'Release', [ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet publish src/StaleBread.App -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:RestoreLockedMode=true -p:NuGetLockFilePath=packages.publish.lock.json -o "dist/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath 'docs/USAGE.md' -Destination "dist/$Runtime/USAGE.md"
    Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.md' -Destination "dist/$Runtime/THIRD-PARTY-NOTICES.md"
    Copy-Item -LiteralPath 'LICENSE' -Destination "dist/$Runtime/LICENSE"
    $licenseOutput = New-Item -ItemType Directory -Path "dist/$Runtime/Licenses" -Force
    foreach ($license in (Get-ChildItem -LiteralPath 'Licenses' -File)) {
        Copy-Item -LiteralPath $license.FullName -Destination $licenseOutput.FullName -Force
    }
    $exeHash = Get-FileHash -LiteralPath "dist/$Runtime/StaleBread.exe" -Algorithm SHA256
    [pscustomobject]@{ File = 'StaleBread.exe'; Algorithm = 'SHA256'; Hash = $exeHash.Hash } |
        ConvertTo-Json | Set-Content "dist/$Runtime/checksum.json"
} finally { Pop-Location }
