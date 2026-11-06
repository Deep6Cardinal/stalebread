[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'StaleBread requires Windows. Run this bootstrap on Windows with the .NET SDK from global.json.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET SDK specified by global.json before running setup.ps1.'
}

Push-Location $PSScriptRoot
try {
    dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'The SDK selected by global.json is unavailable.' }
    dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    Write-Host 'Ready. Run dotnet run --project src/StaleBread.App -c Release --no-build to start the app.'
} finally {
    Pop-Location
}
