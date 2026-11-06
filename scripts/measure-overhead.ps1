param(
    [int]$ProcessId = 0, [long]$CreationFileTimeUtc = 0,
    [ValidateRange(0.05,120)][double]$Minutes = 30,
    [ValidateSet('Baseline','WithApp')][string]$Mode = 'WithApp',
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'WithApp') {
    $app = Get-Process -Id $ProcessId
    if ($app.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $CreationFileTimeUtc) { throw 'App lifetime mismatch' }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StaleBreadMeasurementClock {
 [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
 public static long[] Read() { long i,k,u; if(!GetSystemTimes(out i,out k,out u)) throw new Exception("System time unavailable"); return new[]{i,k,u}; }
}
'@
$winmgmt = (Get-CimInstance Win32_Service -Filter "Name='Winmgmt'").ProcessId
function Get-Providers {
    $map = @{}
    foreach ($p in (Get-Process -Name WmiPrvSE -ErrorAction SilentlyContinue)) {
        try { $map["$($p.Id):$($p.StartTime.ToUniversalTime().ToFileTimeUtc())"] = $p.TotalProcessorTime.TotalSeconds } catch { }
        finally { $p.Dispose() }
    }
    if ($winmgmt) {
        try { $service = Get-Process -Id $winmgmt; $map["$($service.Id):$($service.StartTime.ToUniversalTime().ToFileTimeUtc())"] = $service.TotalProcessorTime.TotalSeconds }
        catch { } finally { if ($service) { $service.Dispose() } }
    }
    return $map
}
$rows = [System.Collections.Generic.List[object]]::new()
$timer = [Diagnostics.Stopwatch]::StartNew()
$previousSystem = [StaleBreadMeasurementClock]::Read()
$previousProviders = Get-Providers
$previousApp = if ($app) { $app.TotalProcessorTime.TotalSeconds } else { 0 }
$previousTime = $timer.Elapsed.TotalSeconds
while ($timer.Elapsed.TotalMinutes -lt $Minutes) {
    Start-Sleep -Seconds 5
    $now = $timer.Elapsed.TotalSeconds
    $seconds = $now - $previousTime
    $system = [StaleBreadMeasurementClock]::Read()
    $total = ($system[1] - $previousSystem[1]) + ($system[2] - $previousSystem[2])
    $idle = $system[0] - $previousSystem[0]
    $providers = Get-Providers
    $providerSeconds = 0.0
    foreach ($key in $providers.Keys) {
        if ($previousProviders.ContainsKey($key)) { $providerSeconds += [Math]::Max(0, $providers[$key] - $previousProviders[$key]) }
    }
    $appCpu = 0.0; $private = 0L; $handles = 0
    if ($app) {
        $app.Refresh()
        if ($app.HasExited -or $app.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $CreationFileTimeUtc) { throw 'Measured app exited or lifetime changed' }
        $appCpu = ($app.TotalProcessorTime.TotalSeconds - $previousApp) / $seconds / [Environment]::ProcessorCount * 100
        $previousApp = $app.TotalProcessorTime.TotalSeconds
        $private = $app.PrivateMemorySize64; $handles = $app.HandleCount
    }
    $rows.Add([pscustomobject]@{Seconds=$now; AppCpuPercent=$appCpu; ProviderCpuPercent=$providerSeconds/$seconds/[Environment]::ProcessorCount*100;
        TotalCpuPercent= $(if ($total -gt 0) { (1-$idle/$total)*100 } else { $null }); AppPrivateBytes=$private; AppHandles=$handles})
    $previousTime=$now; $previousSystem=$system; $previousProviders=$providers
}
[pscustomobject]@{Mode=$Mode; StartedUtc=[datetime]::UtcNow.AddSeconds(-$timer.Elapsed.TotalSeconds); DurationSeconds=$timer.Elapsed.TotalSeconds;
    LogicalProcessors=[Environment]::ProcessorCount; OS=[Environment]::OSVersion.VersionString; ProcessId=$ProcessId; CreationFileTimeUtc=$CreationFileTimeUtc;
    Caveats='Unmatched runs do not establish attributable overhead. Whole service-host CPU may include unrelated services; short-lived providers may be missed. Measurement process adds its own load.';
    Samples=$rows} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath
if ($app) { $app.Dispose() }
