$ErrorActionPreference = 'Stop'

$appName = 'CodexWorldTimeWidget'
$displayName = 'World Time Widget'
$sourceCode = Join-Path $PSScriptRoot 'WorldTimeWidget.cs'
$localAppData = $env:LOCALAPPDATA
if (-not $localAppData) {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
}
$installDir = Join-Path $localAppData $appName
$installedExe = Join-Path $installDir 'WorldTimeWidget.exe'

if (-not (Test-Path -LiteralPath $sourceCode)) {
    throw "Cannot find $sourceCode"
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null

$running = Get-CimInstance Win32_Process |
    Where-Object {
        ($_.ExecutablePath -eq $installedExe) -or
        ($_.CommandLine -like '*CodexWorldTimeWidget*' -and $_.CommandLine -like '*WorldTimeWidget.ps1*')
    }

foreach ($process in $running) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Milliseconds 500

$cscCandidates = @(
    (Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'),
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)

$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) {
    throw 'Cannot find the Windows C# compiler needed to build the widget.'
}

$compileArgs = @(
    '/nologo',
    '/target:winexe',
    ('/out:' + $installedExe),
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    $sourceCode
)
& $csc @compileArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installedExe)) {
    throw 'Widget build failed.'
}

$shell = New-Object -ComObject WScript.Shell

$applicationData = [Environment]::GetFolderPath('ApplicationData')
$startMenuDir = Join-Path $applicationData 'Microsoft\Windows\Start Menu\Programs'
$startupDir = [Environment]::GetFolderPath('Startup')
$desktopDir = [Environment]::GetFolderPath('Desktop')

$shortcutTargets = @(
    (Join-Path $startMenuDir "$displayName.lnk"),
    (Join-Path $startupDir "$displayName.lnk"),
    (Join-Path $desktopDir "$displayName.lnk")
)

foreach ($shortcutPath in $shortcutTargets) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = 'Shows USA Eastern, USA PST, Morocco, and China time.'
    $shortcut.Save()
}

Start-Process -FilePath $installedExe -WorkingDirectory $installDir

[pscustomobject]@{
    Installed = $true
    AppFolder = $installDir
    App = $installedExe
    DesktopShortcut = (Join-Path $desktopDir "$displayName.lnk")
    StartsWithWindows = $true
    UsaTimeZone = 'New York / Eastern Time'
    UsaPstTimeZone = 'Los Angeles / Pacific Standard Time'
    MoroccoTimeZone = 'Casablanca'
    ChinaTimeZone = 'Beijing'
}
