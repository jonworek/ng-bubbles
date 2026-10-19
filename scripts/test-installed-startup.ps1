param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$data = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'NG Bubbles'
$legacy = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'BlueBubbles Desktop'
if ((Test-Path $data) -or (Test-Path $legacy)) { throw 'Startup tests require a disposable Windows account without app data' }
try {
  foreach ($scenario in @('first-run', 'legacy-import')) {
    if ($scenario -eq 'legacy-import') {
      Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
      New-Item $legacy -ItemType Directory | Out-Null
      '{"server":"http://127.0.0.1:1","secret":"invalid-legacy-secret","migrationNotice":""}' | Set-Content (Join-Path $legacy 'settings.json')
      '{"chats":[{"guid":"synthetic-chat","participants":[{"address":"test@example.invalid"}],"lastMessage":{"guid":"synthetic-message","text":"Synthetic cached conversation","dateCreated":1780000000000,"attachments":[]}}],"unread":["synthetic-chat"]}' | Set-Content (Join-Path $legacy 'cache.json')
    }
    $app = Start-Process $Executable -PassThru
    try {
      $found = $false
      for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        if ($app.HasExited) { throw "$scenario exited with $($app.ExitCode)" }
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
        foreach ($window in $windows) {
          $title = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'NG Bubbles settings')
          if ($window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $title)) { $found = $true; break }
        }
        if ($found) { break }
      }
      if (-not $found) { throw "$scenario settings dialog did not appear" }
      Start-Sleep -Seconds 2
      if ($app.HasExited) { throw "$scenario crashed after settings appeared" }
      Write-Output "$scenario installed startup passed"
    } finally {
      if (-not $app.HasExited) { Stop-Process $app.Id -Force }
      if (Test-Path (Join-Path $data 'last-error.txt')) { Get-Content (Join-Path $data 'last-error.txt') }
    }
  }
} finally {
  Remove-Item $data,$legacy -Recurse -Force -ErrorAction SilentlyContinue
}
