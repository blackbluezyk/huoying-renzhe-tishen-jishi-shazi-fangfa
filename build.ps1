$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src\EnergyBeanMonitor.cs'
$output = Join-Path $PSScriptRoot 'bin\EnergyBeanMonitor-Offline.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Windows.Forms.dll ("/out:" + $output) $source
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Output $output
Get-FileHash -LiteralPath $output -Algorithm SHA256 | Format-List Path,Hash
