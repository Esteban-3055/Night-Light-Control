[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
if (-not ((& $dotnetCommand --list-sdks) -match '^10\.')) {
    $cachedSdk = Join-Path $env:USERPROFILE '.codex\tools\dotnet10\dotnet.exe'
    if (Test-Path -LiteralPath $cachedSdk) { $dotnetCommand = $cachedSdk }
    else { throw 'Instala el SDK de .NET 10 para compilar el programa.' }
}
[xml]$project = Get-Content -LiteralPath (Join-Path $repository 'NightLightControl.csproj')
$version = [string]$project.Project.PropertyGroup.Version
& $dotnetCommand run --project (Join-Path $repository 'tests\NightLightControl.Tests.csproj') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw 'Las pruebas fallaron.' }
$publishDirectory = Join-Path $OutputDirectory 'NightLightControl'
& $dotnetCommand publish (Join-Path $repository 'NightLightControl.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'La compilación portable falló.' }
Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination (Join-Path $publishDirectory 'LEEME.md')
Copy-Item -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.txt') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repository 'docs') -Destination $publishDirectory -Recurse -Force
$archive = Join-Path $OutputDirectory "NightLightControl-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -Force -CompressionLevel Optimal
$hashes = foreach ($file in @((Join-Path $publishDirectory 'NightLightControl.exe'), $archive)) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    '{0} *{1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
}
$hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Portable preparado: $archive"
