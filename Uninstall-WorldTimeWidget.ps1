$ErrorActionPreference = 'Stop'

$appName = 'CodexWorldTimeWidget'
$displayName = 'World Time Widget'
$localAppData = $env:LOCALAPPDATA
if (-not $localAppData) {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
}
$installDir = Join-Path $localAppData $appName
$installedExe = Join-Path $installDir 'WorldTimeWidget.exe'
$startupDir = [Environment]::GetFolderPath('Startup')
$desktopDir = [Environment]::GetFolderPath('Desktop')
$applicationData = [Environment]::GetFolderPath('ApplicationData')
$startMenuDir = Join-Path $applicationData 'Microsoft\Windows\Start Menu\Programs'

$shortcutTargets = @(
    (Join-Path $startMenuDir "$displayName.lnk"),
    (Join-Path $startupDir "$displayName.lnk"),
    (Join-Path $desktopDir "$displayName.lnk")
)

$processes = Get-CimInstance Win32_Process |
    Where-Object {
        ($_.ExecutablePath -eq $installedExe) -or
        ($_.CommandLine -like '*CodexWorldTimeWidget*' -and $_.CommandLine -like '*WorldTimeWidget.ps1*')
    }

foreach ($process in $processes) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}

foreach ($shortcutPath in $shortcutTargets) {
    if (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}

if (Test-Path -LiteralPath $installDir) {
    $resolved = (Resolve-Path -LiteralPath $installDir).Path
    $resolvedLocalAppData = (Resolve-Path -LiteralPath $localAppData).Path
    if ($resolved.StartsWith($resolvedLocalAppData, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

[pscustomobject]@{
    Removed = $true
    AppFolder = $installDir
}
