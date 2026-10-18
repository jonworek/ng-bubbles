param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$assets = Get-Content 'native/NgBubbles.Windows/obj/project.assets.json' -Raw | ConvertFrom-Json -AsHashtable
$packages = $assets.packageFolders.Keys | Select-Object -First 1
$licenseDirectory = Join-Path $OutputDirectory 'licenses'
New-Item $licenseDirectory -ItemType Directory -Force | Out-Null
$manifest = @()
foreach ($name in $assets.libraries.Keys) {
    $library = $assets.libraries[$name]
    if ($library.type -ne 'package') { continue }
    $folder = Join-Path $packages $library.path
    $destination = Join-Path $licenseDirectory ($name.Replace('/','-'))
    New-Item $destination -ItemType Directory -Force | Out-Null
    $licenseFiles = Get-ChildItem $folder -File -Recurse | Where-Object { $_.Name -match '(?i)(license|notice|copying)' }
    foreach ($file in $licenseFiles) {
        $relative = [System.IO.Path]::GetRelativePath($folder, $file.FullName)
        $target = Join-Path $destination $relative
        New-Item (Split-Path $target) -ItemType Directory -Force | Out-Null
        Copy-Item $file.FullName $target
    }
    Get-ChildItem $folder -Filter '*.nuspec' | Copy-Item -Destination $destination
    $manifest += @{ package=$name; licenseFiles=@($licenseFiles | ForEach-Object { [System.IO.Path]::GetRelativePath($folder, $_.FullName) }) }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $licenseDirectory 'packages.json')
Copy-Item LICENSE, NATIVE_NOTICES.md -Destination $OutputDirectory -Force

$dotnetNotices = Join-Path $licenseDirectory 'dotnet-runtime'
New-Item $dotnetNotices -ItemType Directory -Force | Out-Null
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $file = Join-Path $env:DOTNET_ROOT $name
    if (-not (Test-Path $file)) { throw "Required .NET runtime notice missing: $name" }
    Copy-Item $file $dotnetNotices
}
