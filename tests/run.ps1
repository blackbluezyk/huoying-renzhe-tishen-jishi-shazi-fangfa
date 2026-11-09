$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'test-artifacts'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$output = Join-Path $work 'OfflineUnitTests.exe'
& $compiler /nologo /target:exe /main:OfflineUnitTests /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Windows.Forms.dll ("/out:" + $output) (Join-Path $root 'src\EnergyBeanMonitor.cs') (Join-Path $PSScriptRoot 'OfflineUnitTests.cs')
if ($LASTEXITCODE -ne 0) { throw "Test compile failed: $LASTEXITCODE" }
& $output (Join-Path $work 'configs')
if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
