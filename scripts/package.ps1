[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = '0.1.0-preview'
& (Join-Path $PSScriptRoot 'build.ps1')
$dist = Join-Path $repoRoot 'dist'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stageName = 'codex-history-archiver-package-' + [Guid]::NewGuid().ToString('N')
$stageRoot = [IO.Path]::GetFullPath((Join-Path $temporaryRoot $stageName))
$allowedPrefix = $temporaryRoot.TrimEnd('\') + '\'
if (-not $stageRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path.' }
New-Item -ItemType Directory -Path $stageRoot | Out-Null
try {
    $sourceStage = Join-Path $stageRoot 'CodexHistoryArchiver'
    New-Item -ItemType Directory -Path $sourceStage | Out-Null
    $sourceNames = @('.gitignore', 'LICENSE', 'README.md', 'README.en.md', 'CHANGELOG.md', 'CodexHistoryArchiver.csproj')
    foreach ($name in $sourceNames) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $sourceStage
    }
    # Only original source/build/test text files are allowed into the source ZIP.
    foreach ($folder in @('src', 'scripts', 'tests', '.github')) {
        $directory = Join-Path $repoRoot $folder
        foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File -Force) {
            if ($file.Extension -notin @('.cs', '.manifest', '.ps1', '.yml', '.md')) {
                throw "Unexpected source artifact: $($file.Name)"
            }
            $relative = $file.FullName.Substring($repoRoot.Length + 1)
            $destination = Join-Path $sourceStage $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    $sourceZip = Join-Path $dist "CodexHistoryArchiver-$version-source.zip"
    Compress-Archive -LiteralPath $sourceStage -DestinationPath $sourceZip -Force
    $runtimeStage = Join-Path $stageRoot 'CodexHistoryArchiver-Windows'
    New-Item -ItemType Directory -Path $runtimeStage | Out-Null
    foreach ($name in @('CodexHistoryArchiver.exe', 'README.md', 'README.en.md', 'LICENSE', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $dist $name) -Destination $runtimeStage
    }
    $runtimeZip = Join-Path $dist "CodexHistoryArchiver-$version-windows.zip"
    Compress-Archive -LiteralPath $runtimeStage -DestinationPath $runtimeZip -Force
    $hashLines = foreach ($file in @($sourceZip, $runtimeZip)) {
        $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
        $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file)
    }
    [IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), [string[]]$hashLines, [Text.Encoding]::ASCII)
    Write-Output 'Source ZIP, Windows ZIP, and SHA256SUMS.txt are ready in dist/.'
}
finally {
    # Delete only this run's verified, uniquely named staging directory.
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    if ($resolvedStage.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedStage) -eq $stageName -and
        (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
