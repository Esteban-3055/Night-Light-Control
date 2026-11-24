[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$dotnetCommand = (Get-Command dotnet).Source
if (-not ((& $dotnetCommand --list-sdks) -match '^10\.')) { $dotnetCommand = Join-Path $env:USERPROFILE '.codex\tools\dotnet10\dotnet.exe' }
[xml]$project = Get-Content (Join-Path $repository 'NightLightControl.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$payload = Join-Path $OutputDirectory "NightLightControl-$version-win-x64.zip"
$setupOutput = Join-Path $OutputDirectory 'setup-build'
& $dotnetCommand publish (Join-Path $repository 'Setup\Setup.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None "-p:PayloadZip=$payload" -o $setupOutput --nologo
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del instalador.' }
$setup = Join-Path $OutputDirectory "NightLightControl-$version-Setup.exe"
Copy-Item -LiteralPath (Join-Path $setupOutput 'Setup.exe') -Destination $setup -Force
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
('{0} *{1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($setup)) | Add-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
