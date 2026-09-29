param([string]$ReportPath)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '.NET Framework 4.x C# compiler was not found. Run these tests on Windows with .NET Framework installed.'
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testName = 'CodexHistoryArchiver-Tests-' + [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempBase $testName))
$expectedRoot = $testRoot
$exitCode = 1
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot '.synthetic-test-root') -Value 'synthetic only' -Encoding UTF8
    $fakeExecutable = Join-Path $testRoot 'FakeCodex.exe'
    $testExecutable = Join-Path $testRoot 'ClientTests.exe'
    $references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 "/out:$fakeExecutable" @references (Join-Path $PSScriptRoot 'FakeCodex.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic server compilation failed.' }
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 /main:CodexHistoryArchive.Tests.ClientTests "/out:$testExecutable" @references (Join-Path $repositoryRoot 'src\ArchiveManager.cs') (Join-Path $PSScriptRoot 'ClientTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Client test compilation failed.' }
    & $testExecutable $fakeExecutable $testRoot
    $exitCode = $LASTEXITCODE
    if ($ReportPath) {
        $reportDestination = [IO.Path]::GetFullPath($ReportPath)
        $reportParent = Split-Path -Parent $reportDestination
        if (-not (Test-Path -LiteralPath $reportParent -PathType Container)) { throw 'The report destination directory must already exist.' }
        Copy-Item -LiteralPath (Join-Path $testRoot 'test-report.json') -Destination $reportDestination
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedRoot = (Resolve-Path -LiteralPath $testRoot).Path
        if (-not [String]::Equals($resolvedRoot, $expectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not $resolvedRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedRoot) -ne $testName -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRoot '.synthetic-test-root') -PathType Leaf)) {
            throw 'Refusing cleanup: isolated test directory validation failed.'
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
exit $exitCode
