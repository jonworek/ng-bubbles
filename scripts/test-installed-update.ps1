param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path $Installer).Path
$data = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'NG Bubbles'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'NG Bubbles.lnk'
$preferences = 'HKCU:\Software\Jon Worek Technology LLC\NG Bubbles\Installer'
$install = Join-Path $env:RUNNER_TEMP 'NG Bubbles update test'
$exe = Join-Path $install 'NG Bubbles.exe'
$report = Join-Path $env:RUNNER_TEMP 'ng-update.json'
if (Test-Path $data) { throw 'Update tests require a disposable Windows account without app data' }
function InstalledProcesses {
  @(Get-Process | Where-Object { $_.Path -eq $exe })
}
function WaitInstaller($process) {
  if (-not $process.WaitForExit(90000)) { Stop-Process $process.Id -Force; throw 'Update installer timed out' }
  if ($process.ExitCode -ne 0) { throw "Update installer exited with $($process.ExitCode)" }
}
try {
  foreach ($shortcut in @($false, $true)) {
    Remove-Item $desktop -Force -ErrorAction SilentlyContinue
    $task = '/TASKS=""'
    if ($shortcut) { $task = '/TASKS="desktopicon"' }
    $initial = Start-Process $Installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $task, "/DIR=`"$install`"") -PassThru
    WaitInstaller $initial
    if ((Test-Path $desktop) -ne $shortcut) { throw 'Initial desktop shortcut choice was not applied' }
    New-Item $data -ItemType Directory -Force | Out-Null
    '{"drafts":{"synthetic-chat":"Draft retained through update"}}' | Set-Content (Join-Path $data 'cache.json')
    '{"width":1280,"height":800,"maximized":false}' | Set-Content (Join-Path $data 'window.json')
    $cacheHash = (Get-FileHash (Join-Path $data 'cache.json')).Hash
    New-Item $preferences -Force | Out-Null
    New-ItemProperty $preferences -Name LaunchOnFinish -Value 0 -PropertyType DWord -Force | Out-Null
    $parent = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -PassThru
    $log = Join-Path $env:RUNNER_TEMP "ng-update-$shortcut.log"
    try {
      $update = Start-Process $Installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/NGUPDATE=1', "/NGWAITPID=$($parent.Id)", "/DIR=`"$install`"", "/LOG=`"$log`"") -PassThru
      $waiting = $false
      for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 200
        if ($update.HasExited) { throw 'Update exited before the old process closed' }
        if ((Test-Path $log) -and (Select-String -Path $log -SimpleMatch 'Waiting for NG Bubbles process' -Quiet)) { $waiting = $true; break }
      }
      if (-not $waiting -or $parent.HasExited -or (InstalledProcesses).Count) { throw 'Update did not wait for the previous process' }
      Stop-Process $parent.Id -Force
      WaitInstaller $update
      $launched = @()
      for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 200
        $launched = @(InstalledProcesses)
        if ($launched.Count) { break }
      }
      if ($launched.Count -ne 1) { throw 'Update did not automatically reopen exactly one app instance' }
      Start-Sleep -Seconds 2
      if ($launched[0].HasExited) { throw 'Updated app crashed after automatic restart' }
      if ((Test-Path $desktop) -ne $shortcut) { throw 'Silent update changed the desktop shortcut choice' }
      if ((Get-ItemProperty $preferences).LaunchOnFinish -ne 0) { throw 'Silent update overwrote the normal installer launch preference' }
      if ((Get-FileHash (Join-Path $data 'cache.json')).Hash -ne $cacheHash) { throw 'Silent update changed cached drafts' }
      if (-not (Test-Path (Join-Path $data 'window.json'))) { throw 'Silent update removed saved window settings' }
      Write-Output "Silent update retained shortcut=$shortcut, cached draft, launch preference and restarted the installed app"
    } finally {
      if (-not $parent.HasExited) { Stop-Process $parent.Id -Force }
      InstalledProcesses | Stop-Process -Force
    }
  }
  $invalid = Start-Process $Installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NGUPDATE=1', '/NGWAITPID=0', "/DIR=`"$install`"") -PassThru
  if (-not $invalid.WaitForExit(30000)) { Stop-Process $invalid.Id -Force; throw 'Invalid update command did not stop' }
  if ($invalid.ExitCode -eq 0 -or (InstalledProcesses).Count) { throw 'Invalid update command installed or reopened the app' }
  @{ silentUpdate = $true; waitsForExit = $true; restartsApp = $true; shortcutChoices = 2; launchPreferenceRetained = $true; draftsRetained = $true; invalidCommandRejected = $true } | ConvertTo-Json | Set-Content $report
} finally {
  InstalledProcesses | Stop-Process -Force
  Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
