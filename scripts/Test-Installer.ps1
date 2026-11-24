[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'dist' }
[xml]$project = Get-Content (Join-Path $repository 'NightLightControl.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$setup = Join-Path $OutputDirectory "NightLightControl-$version-Setup.exe"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NightLightControl'
if (Test-Path -LiteralPath $uninstallKey) { throw 'Ya existe una instalación registrada; no se ejecutará la prueba sobre ella.' }
if (Get-Process NightLightControl -ErrorAction SilentlyContinue) { throw 'Cierra Night Light Control con Salir y restaurar antes de la prueba.' }
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('NightLightControl-install-test-' + [Guid]::NewGuid().ToString('N'))
$profileFile = Join-Path $env:LOCALAPPDATA 'NightLightControl\settings.json'
$before = if (Test-Path -LiteralPath $profileFile) { (Get-FileHash -LiteralPath $profileFile -Algorithm SHA256).Hash } else { $null }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startup = (Get-ItemProperty -LiteralPath $runKey -Name NightLightControl -ErrorAction SilentlyContinue).NightLightControl
$arguments = '--silent --dir "' + $testDirectory + '" --startup false --desktop false'
try {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 0) { throw 'Falló la instalación/reinstalación de prueba.' }
        $entry = Get-ItemProperty -LiteralPath $uninstallKey
        if ($entry.DisplayVersion -ne $version -or $entry.InstallLocation -ne $testDirectory) { throw 'Registro de instalación incorrecto.' }
        if (-not (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Night Light Control.lnk'))) { throw 'Falta acceso en Inicio.' }
    }
    Start-Process -FilePath (Join-Path $testDirectory 'NightLightControl.exe') -ArgumentList '--uninstall --silent' -Wait -WindowStyle Hidden
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath $uninstallKey) -and $deadline.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath $uninstallKey) { throw 'La desinstalación de prueba no terminó.' }
    if (Test-Path -LiteralPath (Join-Path $testDirectory 'NightLightControl.exe')) { throw 'El programa no se eliminó.' }
    $after = if (Test-Path -LiteralPath $profileFile) { (Get-FileHash -LiteralPath $profileFile -Algorithm SHA256).Hash } else { $null }
    if ($before -ne $after) { throw 'La configuración cambió durante la prueba.' }
    Write-Output 'PASS: install, reinstall, Start menu, registered version, uninstall and preserved profiles.'
}
finally {
    if ($startup) { Set-ItemProperty -LiteralPath $runKey -Name NightLightControl -Value $startup }
}
