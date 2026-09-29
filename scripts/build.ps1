[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 on Windows first.' }
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$executable = Join-Path $dist 'CodexHistoryArchiver.exe'
$sources = @((Join-Path $repoRoot 'src\ArchiveManager.cs'), (Join-Path $repoRoot 'src\AssemblyInfo.cs'))
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /warnaserror+ /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/win32manifest:$(Join-Path $repoRoot 'src\app.manifest')" "/out:$executable" $sources
if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
foreach ($name in @('README.md', 'README.en.md', 'LICENSE', 'CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $dist -Force
}
Write-Output 'Build succeeded: dist/CodexHistoryArchiver.exe'

